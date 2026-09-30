namespace AirSend.Core.Localization;

/// <summary>
/// Messages of the failures that reach the user (a receiver that cannot be reached,
/// a pairing that is rejected, a capture that dies, an update that does not fit).
/// Same three-language table as the log; the Spanish column keeps the wording the
/// upstream Rust build used so support notes stay comparable.
/// </summary>
internal static class ErrorMessages
{
    private static readonly Dictionary<string, (string Es, string En, string Zh)> Table =
        new(StringComparer.Ordinal)
        {
            // ---- Reachability ----------------------------------------------------------
            ["error.probe.invalid_ip"] = (
                "IP inválida: {ip}",
                "invalid IP: {ip}",
                "IP 无效：{ip}"),
            ["error.probe.connect_timeout"] = (
                "se agotó el tiempo tras {seconds} s conectando a {endpoint}",
                "timed out after {seconds} s connecting to {endpoint}",
                "连接 {endpoint} 超时（{seconds} 秒）"),
            ["error.probe.connect_failed"] = (
                "no pude conectar a {endpoint}: {err}",
                "could not connect to {endpoint}: {err}",
                "无法连接 {endpoint}：{err}"),
            ["error.probe.response_timeout"] = (
                "se agotó el tiempo tras {seconds} s esperando la respuesta RTSP",
                "timed out after {seconds} s waiting for the RTSP response",
                "等待 RTSP 响应超时（{seconds} 秒）"),
            ["error.probe.not_rtsp"] = (
                "la respuesta no es de RTSP/AirPlay: {line}",
                "the reply is not an RTSP/AirPlay response: {line}",
                "对方返回的不是 RTSP/AirPlay 响应：{line}"),

            // ---- Manual address --------------------------------------------------------
            ["error.endpoint.empty"] = (
                "el campo está vacío",
                "the field is empty",
                "地址为空"),
            ["error.endpoint.zero_port"] = (
                "el puerto 0 no es válido",
                "port 0 is not valid",
                "端口 0 无效"),
            ["error.endpoint.invalid"] = (
                "'{input}' no es una IP ni una IP:puerto",
                "'{input}' is not an IP or IP:port endpoint",
                "「{input}」不是 IP 或 IP:端口 格式"),

            // ---- Streaming -------------------------------------------------------------
            ["error.stream.no_ip"] = (
                "el dispositivo {name} no tiene dirección IP",
                "device {name} has no IP address",
                "设备 {name} 没有 IP 地址"),
            ["error.stream.no_route"] = (
                "no hay ninguna ruta con dirección IP",
                "no route with an IP address is available",
                "没有可用的带 IP 地址的连接方式"),
            ["error.stream.setup1_failed"] = (
                "SETUP fase 1 devolvió {status}",
                "SETUP phase 1 returned {status}",
                "SETUP 第一阶段返回 {status}"),
            ["error.stream.setup2_failed"] = (
                "SETUP fase 2 devolvió {status}",
                "SETUP phase 2 returned {status}",
                "SETUP 第二阶段返回 {status}"),

            // ---- Pairing ---------------------------------------------------------------
            ["error.pairing.setup_m2_failed"] = (
                "pair-setup M2 falló: {detail}",
                "pair-setup M2 failed: {detail}",
                "pair-setup M2 失败：{detail}"),
            ["error.pairing.setup_m2_no_key"] = (
                "pair-setup M2 no trajo la clave pública del receptor",
                "pair-setup M2 carried no server public key",
                "pair-setup M2 里没有接收端公钥"),
            ["error.pairing.setup_m2_no_salt"] = (
                "pair-setup M2 no trajo el salt",
                "pair-setup M2 carried no salt",
                "pair-setup M2 里没有 salt"),
            ["error.pairing.setup_m4_failed"] = (
                "pair-setup M4 falló: {detail}",
                "pair-setup M4 failed: {detail}",
                "pair-setup M4 失败：{detail}"),
            ["error.pairing.setup_m4_no_proof"] = (
                "pair-setup M4 no trajo la prueba",
                "pair-setup M4 carried no proof",
                "pair-setup M4 里没有校验值"),
            ["error.pairing.setup_m4_proof_mismatch"] = (
                "la prueba de pair-setup M4 no coincide",
                "the pair-setup M4 proof did not match",
                "pair-setup M4 的校验值不匹配"),
            ["error.pairing.verify_m2_no_key"] = (
                "pair-verify M2 no trajo la clave pública del receptor",
                "pair-verify M2 carried no server public key",
                "pair-verify M2 里没有接收端公钥"),
            ["error.pairing.verify_m2_no_data"] = (
                "pair-verify M2 no trajo datos cifrados",
                "pair-verify M2 carried no encrypted data",
                "pair-verify M2 里没有加密数据"),
            ["error.pairing.verify_m2_no_id"] = (
                "pair-verify M2 no trajo el identificador",
                "pair-verify M2 carried no identifier",
                "pair-verify M2 里没有标识符"),
            ["error.pairing.verify_m2_no_signature"] = (
                "pair-verify M2 no trajo la firma",
                "pair-verify M2 carried no signature",
                "pair-verify M2 里没有签名"),
            ["error.pairing.verify_m2_bad_signature"] = (
                "la firma de pair-verify M2 no se pudo verificar",
                "the pair-verify M2 signature did not verify",
                "pair-verify M2 的签名验证失败"),
            ["error.pairing.verify_m4_failed"] = (
                "pair-verify M4 falló: {detail}",
                "pair-verify M4 failed: {detail}",
                "pair-verify M4 失败：{detail}"),
            ["error.pairing.status"] = (
                "{what} devolvió el estado {status}",
                "{what} returned status {status}",
                "{what} 返回状态 {status}"),
            ["error.pairing.empty_body"] = (
                "{what} vino sin cuerpo",
                "{what} carried an empty body",
                "{what} 返回内容为空"),

            // ---- Capture ---------------------------------------------------------------
            ["error.capture.no_enumerator"] = (
                "no encuentro MMDeviceEnumerator (¿está disponible el audio del sistema?)",
                "MMDeviceEnumerator is not registered (is system audio available?)",
                "找不到 MMDeviceEnumerator（系统音频接口不可用？）"),
            ["error.capture.enumerator_failed"] = (
                "no pude crear MMDeviceEnumerator",
                "could not create MMDeviceEnumerator",
                "无法创建 MMDeviceEnumerator"),
            ["error.capture.interrupted"] = (
                "la captura de audio se interrumpió (dispositivo desconectado o fallo del controlador)",
                "audio capture was interrupted (device unplugged or driver failure)",
                "采集被中断（设备被拔出或驱动出错）"),
            ["error.capture.failed"] = (
                "la captura de audio falló: {err}",
                "audio capture failed: {err}",
                "采集失败：{err}"),
            ["error.pump.failed"] = (
                "el envío de audio falló: {err}",
                "sending audio failed: {err}",
                "音频发送失败：{err}"),

            // ---- RTSP ------------------------------------------------------------------
            ["error.rtsp.not_connected"] = (
                "el cliente RTSP no está conectado",
                "the RTSP client is not connected",
                "RTSP 客户端尚未连接"),
            ["error.rtsp.closed"] = (
                "el receptor cerró la conexión RTSP",
                "the receiver closed the RTSP connection",
                "接收端关闭了 RTSP 连接"),

            // ---- Updates ---------------------------------------------------------------
            ["error.update.no_package"] = (
                "la versión {version} no trae un paquete para esta instalación ({build})",
                "release {version} has no package for this installation ({build})",
                "版本 {version} 里没有适用于本机安装（{build}）的包"),
            ["error.update.package_no_executable"] = (
                "el paquete no contiene AirSend.exe",
                "the package does not contain AirSend.exe",
                "安装包里没有 AirSend.exe"),
            ["error.update.package_version"] = (
                "el paquete trae la versión {found} y no {expected}",
                "the package contains version {found}, not {expected}",
                "包里是 {found} 版，而不是 {expected} 版"),
            ["error.update.package_arch"] = (
                "el paquete es para {found} y esta instalación es {expected}",
                "the package is for {found} but this installation is {expected}",
                "安装包是 {found} 版本，而本机是 {expected}"),
            ["error.update.package_flavor"] = (
                "el paquete es {found} y esta instalación es {expected}",
                "the package is {found} but this installation is {expected}",
                "安装包是「{found}」，而本机装的是「{expected}」"),
            ["error.update.download_failed"] = (
                "no pude descargar {package}: {err}",
                "could not download {package}: {err}",
                "下载 {package} 失败：{err}"),
            ["error.update.attempts_exhausted"] = (
                "se agotaron los intentos",
                "all attempts failed",
                "重试次数已用完"),
        };

    public static string Format(string? language, string key, object? parameters) =>
        MessageTable.Format(language, Table, key, parameters);

    internal static IReadOnlyCollection<string> Keys => Table.Keys;
}
