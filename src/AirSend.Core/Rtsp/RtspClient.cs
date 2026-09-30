using System.Net;
using System.Net.Sockets;
using System.Text;
using AirSend.Core.Crypto;
using AirSend.Core.Logging;

namespace AirSend.Core.Rtsp;

public sealed class RtspRequest(string method, string uri)
{
    public string Method { get; } = method;

    public string Uri { get; } = uri;

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public byte[]? Body { get; private set; }

    public RtspRequest WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public RtspRequest WithBody(byte[] body, string contentType)
    {
        Body = body;
        Headers["Content-Type"] = contentType;
        return this;
    }

    public byte[] Serialize(uint cseq)
    {
        var builder = new StringBuilder();
        builder.Append(Method).Append(' ').Append(Uri).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(cseq).Append("\r\n");

        if (Body is not null)
        {
            builder.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
        }

        foreach (KeyValuePair<string, string> header in Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("\r\n");

        byte[] head = Encoding.UTF8.GetBytes(builder.ToString());
        if (Body is null)
        {
            return head;
        }

        byte[] message = new byte[head.Length + Body.Length];
        head.CopyTo(message, 0);
        Body.CopyTo(message, head.Length);
        return message;
    }
}

public sealed class RtspResponse
{
    public required int StatusCode { get; init; }

    public required string Reason { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public byte[]? Body { get; init; }

    public string BodyText => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);

    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;

    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// RTSP over TCP client with optional HomeKit control-channel encryption.
/// Port of <c>RtspConnection</c> in <c>airplay-rtsp/src/connection.rs</c>: the
/// receiver speaks plain RTSP until pairing completes, then every request and
/// response is wrapped in the <see cref="ControlCipher"/> framing.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private readonly IPEndPoint _endpoint;
    private readonly List<byte> _receiveBuffer = new();
    private readonly Dictionary<string, string> _sessionHeaders = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private ControlCipher? _cipher;
    private uint _cseq;

    public RtspClient(IPAddress address, ushort port)
    {
        _endpoint = new IPEndPoint(address, port);
    }

    public string BaseUri { get; set; } = "/";

    public bool IsEncrypted => _cipher is not null;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _client = new TcpClient(_endpoint.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await _client.ConnectAsync(_endpoint, cts.Token).ConfigureAwait(false);
        _client.NoDelay = true;
        _stream = _client.GetStream();
    }

    public void SetCipher(ControlCipher cipher) => _cipher = cipher;

    public void AddSessionHeader(string name, string value) => _sessionHeaders[name] = value;

    public void SetSessionId(string sessionId) => _sessionHeaders["Session"] = sessionId;

    public async Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("RTSP client is not connected");
        }

        foreach (KeyValuePair<string, string> header in _sessionHeaders)
        {
            request.Headers.TryAdd(header.Key, header.Value);
        }

        byte[] plaintext = request.Serialize(++_cseq);
        byte[] payload = _cipher is null ? plaintext : _cipher.Encrypt(plaintext);

        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        AppLog.Debug("log.rtsp.request", new
        {
            method = request.Method,
            uri = request.Uri,
            cseq = _cseq,
            bytes = plaintext.Length,
        });

        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RtspResponse> SendAsync(string method, string uri, byte[]? body = null, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var request = new RtspRequest(method, uri);
        if (body is not null)
        {
            request.WithBody(body, contentType ?? "application/x-apple-binary-plist");
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[4096];

        while (true)
        {
            if (TryParseResponse(out RtspResponse? response))
            {
        AppLog.Debug("log.rtsp.response", new
        {
            status = response!.StatusCode,
            reason = response.Reason,
            bytes = _receiveBuffer.Count,
        });
                return response;
            }

            if (_cipher is not null)
            {
                await ReadEncryptedFrameAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int read = await _stream!.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("RTSP connection closed by the receiver");
                }

                _receiveBuffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }
        }
    }

    private async Task ReadEncryptedFrameAsync(CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(2, cancellationToken).ConfigureAwait(false);
        int blockLength = _receiveBuffer[0] | (_receiveBuffer[1] << 8);
        _receiveBuffer.RemoveRange(0, 2);

        await ReadExactlyAsync(blockLength + 16, cancellationToken).ConfigureAwait(false);
        byte[] frame = _receiveBuffer.Take(blockLength + 16).ToArray();
        _receiveBuffer.RemoveRange(0, blockLength + 16);

        byte[] plaintext = _cipher!.DecryptBlock(frame, (ushort)blockLength);
        _receiveBuffer.AddRange(plaintext);
    }

    private async Task ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var chunk = new byte[4096];
        while (_receiveBuffer.Count < count)
        {
            int read = await _stream!.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("RTSP connection closed by the receiver");
            }

            _receiveBuffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
    }

    private bool TryParseResponse(out RtspResponse? response)
    {
        response = null;
        int headerEnd = FindHeaderEnd(_receiveBuffer);
        if (headerEnd < 0)
        {
            return false;
        }

        string headerText = Encoding.UTF8.GetString(_receiveBuffer.ToArray(), 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        string statusLine = lines[0];
        string[] statusParts = statusLine.Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int statusCode))
        {
            throw new FormatException($"malformed RTSP status line: {statusLine}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int separator = lines[i].IndexOf(':');
            if (separator > 0)
            {
                headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
            }
        }

        int contentLength = headers.TryGetValue("Content-Length", out string? value) &&
                            int.TryParse(value, out int parsed)
            ? parsed
            : 0;

        int bodyStart = headerEnd + 4;
        if (_receiveBuffer.Count - bodyStart < contentLength)
        {
            return false;
        }

        byte[] body = _receiveBuffer.GetRange(bodyStart, contentLength).ToArray();
        _receiveBuffer.RemoveRange(0, bodyStart + contentLength);

        response = new RtspResponse
        {
            StatusCode = statusCode,
            Reason = statusParts.Length > 2 ? statusParts[2] : string.Empty,
            Headers = headers,
            Body = contentLength > 0 ? body : null,
        };
        return true;
    }

    private static int FindHeaderEnd(List<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Closing a dead socket is fine.
            }
        }

        _client?.Dispose();
        _client = null;
        _stream = null;
    }
}
