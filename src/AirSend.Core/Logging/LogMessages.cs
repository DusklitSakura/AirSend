using AirSend.Core.Localization;

namespace AirSend.Core.Logging;

/// <summary>
/// Message catalog for the log. The Spanish column is the text the upstream Rust
/// build writes (the log used to be Spanish no matter which language the interface
/// was in, because the strings were ported verbatim); the log now follows the
/// language the app sets in <see cref="AppLog.Language"/>.
/// </summary>
/// <remarks>
/// One entry per key holds all three languages, so a key can never be added in one
/// language only. Placeholders are named after the properties of the anonymous
/// object passed to the log call, exactly like the UI string table:
/// <c>AppLog.Info("log.rtsp.connected", new { address = "192.168.1.17:7000" })</c>.
/// </remarks>
internal static class LogMessages
{
    private static readonly Dictionary<string, (string Es, string En, string Zh)> Table =
        new(StringComparer.Ordinal)
        {
            // ---- Application lifecycle -------------------------------------------------
            ["log.logs.directory"] = (
                "→ logs a {path}",
                "→ logs to {path}",
                "→ 日志写到 {path}"),
            ["log.app.starting"] = (
                "AirSend (WinUI 3) iniciando",
                "AirSend (WinUI 3) starting",
                "AirSend (WinUI 3) 正在启动"),
            ["log.app.language"] = (
                "idioma de la interfaz: {language} (sistema: {system})",
                "interface language: {language} (system: {system})",
                "界面语言：{language}（系统：{system}）"),
            ["log.app.startup_tray"] = (
                "inicio automático: la ventana queda en la bandeja",
                "autostart: the window stays in the tray",
                "开机自启动：窗口留在托盘"),
            ["log.app.unhandled"] = (
                "excepción no controlada",
                "unhandled exception",
                "未处理的异常"),
            ["log.tray.unavailable"] = (
                "bandeja del sistema no disponible: {err}",
                "system tray unavailable: {err}",
                "系统托盘不可用：{err}"),
            ["log.tray.add_failed"] = (
                "no pude añadir el icono a la bandeja (error {code}); la ventana se cerrará al pulsar X",
                "could not add the tray icon (error {code}); closing the window will exit",
                "无法添加托盘图标（错误 {code}）；此时关闭窗口会直接退出"),
            ["log.instance.already_running"] = (
                "ya hay otra instancia de AirSend en ejecución",
                "another AirSend instance is already running",
                "已经有一个 AirSend 在运行"),
            ["log.instance.wake_failed"] = (
                "no pude mostrar la ventana existente: {err}",
                "could not show the existing window: {err}",
                "无法唤出已有窗口：{err}"),
            ["log.dialog.failed"] = (
                "no pude mostrar el diálogo: {err}",
                "could not show the dialog: {err}",
                "无法显示对话框：{err}"),
            ["log.open_failed"] = (
                "no pude abrir {path}: {err}",
                "could not open {path}: {err}",
                "无法打开 {path}：{err}"),

            // ---- Settings --------------------------------------------------------------
            ["log.settings.read_failed"] = (
                "no pude leer {path}: {err}",
                "could not read {path}: {err}",
                "无法读取 {path}：{err}"),
            ["log.settings.write_failed"] = (
                "no pude guardar {path}: {err}",
                "could not save {path}: {err}",
                "无法写入 {path}：{err}"),
            ["log.startup.read_failed"] = (
                "no pude leer el inicio automático: {err}",
                "could not read the autostart entry: {err}",
                "无法读取开机自启动设置：{err}"),
            ["log.startup.updated"] = (
                "ruta de inicio automático actualizada a la copia actual",
                "autostart entry points at this copy again",
                "开机自启动的路径已更新为当前这份程序"),
            ["log.update.policy"] = (
                "comprobación de actualizaciones: {policy}",
                "update check policy: {policy}",
                "更新检查频率：{policy}"),

            // ---- Discovery -------------------------------------------------------------
            ["log.discovery.browsing"] = (
                "discovery: browsing _airplay._tcp y _raop._tcp",
                "discovery: browsing _airplay._tcp and _raop._tcp",
                "发现：正在浏览 _airplay._tcp 与 _raop._tcp"),
            ["log.discovery.stopped"] = (
                "discovery: detenido",
                "discovery: stopped",
                "发现：已停止"),
            ["log.discovery.cached"] = (
                "discovery ya en curso, replayé {count} cacheados",
                "discovery already running, replayed {count} cached devices",
                "发现流程已在运行，回放 {count} 台缓存设备"),
            ["log.discovery.found"] = (
                "discovered {service}: {device}",
                "discovered {service}: {device}",
                "发现 {service}：{device}"),
            ["log.mdns.unicast_socket"] = (
                "mDNS: socket de consulta unicast en el puerto {port}",
                "mDNS: unicast query socket on port {port}",
                "mDNS：单播查询套接字使用端口 {port}"),
            ["log.mdns.no_unicast_socket"] = (
                "mDNS: sin socket unicast ({error})",
                "mDNS: no unicast socket ({error})",
                "mDNS：没有可用的单播套接字（{error}）"),
            ["log.mdns.socket_ready"] = (
                "mDNS: socket {family} listo",
                "mDNS: {family} socket ready",
                "mDNS：{family} 套接字就绪"),
            ["log.mdns.socket_unavailable"] = (
                "mDNS: socket {family} no disponible ({error})",
                "mDNS: {family} socket unavailable ({error})",
                "mDNS：{family} 套接字不可用（{error}）"),
            ["log.mdns.multicast_rejected"] = (
                "mDNS: {nic} rechazó el grupo multicast",
                "mDNS: {nic} rejected the multicast group",
                "mDNS：{nic} 拒绝加入组播组"),
            ["log.mdns.joined"] = (
                "mDNS: unido a 224.0.0.251 en {nic} ({address})",
                "mDNS: joined 224.0.0.251 on {nic} ({address})",
                "mDNS：已在 {nic}（{address}）加入 224.0.0.251"),
            ["log.mdns.unicast_send_failed"] = (
                "mDNS: fallo al enviar la consulta unicast",
                "mDNS: sending the unicast query failed",
                "mDNS：发送单播查询失败"),
            ["log.mdns.send_failed"] = (
                "mDNS: fallo al enviar la consulta",
                "mDNS: sending the query failed",
                "mDNS：发送查询失败"),
            ["log.mdns.received"] = (
                "mDNS: recibidos {bytes} bytes de {family}",
                "mDNS: received {bytes} bytes from {family}",
                "mDNS：从 {family} 收到 {bytes} 字节"),
            ["log.mdns.discarded"] = (
                "mDNS: mensaje descartado ({type}: {err})",
                "mDNS: dropped a message ({type}: {err})",
                "mDNS：丢弃一条报文（{type}：{err}）"),

            // ---- Pairing ---------------------------------------------------------------
            ["log.pairing.setup_done"] = (
                "pair-setup transient completado (M1-M4)",
                "transient pair-setup completed (M1-M4)",
                "瞬态 pair-setup 完成（M1-M4）"),
            ["log.pairing.verify_done"] = (
                "pair-verify completado (M1-M4)",
                "pair-verify completed (M1-M4)",
                "pair-verify 完成（M1-M4）"),

            // ---- RTSP / session --------------------------------------------------------
            ["log.rtsp.connected"] = (
                "RTSP conectado a {address}",
                "RTSP connected to {address}",
                "RTSP 已连接到 {address}"),
            ["log.rtsp.request"] = (
                "RTSP → {method} {uri} (CSeq {cseq}, {bytes} bytes)",
                "RTSP → {method} {uri} (CSeq {cseq}, {bytes} bytes)",
                "RTSP → {method} {uri}（CSeq {cseq}，{bytes} 字节）"),
            ["log.rtsp.response"] = (
                "RTSP ← {status} {reason} ({bytes} bytes en búfer)",
                "RTSP ← {status} {reason} ({bytes} bytes buffered)",
                "RTSP ← {status} {reason}（缓冲 {bytes} 字节）"),
            ["log.rtsp.options"] = (
                "OPTIONS cifrado → {status} ({methods})",
                "encrypted OPTIONS → {status} ({methods})",
                "加密 OPTIONS → {status}（{methods}）"),
            ["log.rtsp.no_public"] = (
                "sin Public",
                "no Public header",
                "无 Public 头"),
            ["log.rtsp.events_connected"] = (
                "conexión de eventos establecida con el puerto {port}",
                "event channel established on port {port}",
                "已建立事件通道，端口 {port}"),
            ["log.rtsp.events_failed"] = (
                "no pude conectar al puerto de eventos {port}: {err}",
                "could not connect to the event port {port}: {err}",
                "无法连接事件端口 {port}：{err}"),
            ["log.setup.retrying"] = (
                "SETUP fase 2 → {status}, reintentando una vez",
                "SETUP phase 2 → {status}, retrying once",
                "SETUP 第二阶段 → {status}，重试一次"),
            ["log.setup.done"] = (
                "SETUP completado: audio → {address}:{dataPort}, control ← {controlPort} (control del receptor {receiverControlPort})",
                "SETUP complete: audio → {address}:{dataPort}, control ← {controlPort} (receiver control {receiverControlPort})",
                "SETUP 完成：音频 → {address}:{dataPort}，控制 ← {controlPort}（接收端控制端口 {receiverControlPort}）"),
            ["log.rtsp.record"] = (
                "RECORD → {status} {reason}",
                "RECORD → {status} {reason}",
                "RECORD → {status} {reason}"),
            ["log.rtsp.flush"] = (
                "FLUSH → {status} (rtptime 0)",
                "FLUSH → {status} (rtptime 0)",
                "FLUSH → {status}（rtptime 0）"),
            ["log.control.socket_error"] = (
                "control socket: {err}",
                "control socket: {err}",
                "控制套接字：{err}"),
            ["log.control.received"] = (
                "control ← {bytes} bytes, tipo {type}, de {endpoint}",
                "control ← {bytes} bytes, type {type}, from {endpoint}",
                "控制 ← {bytes} 字节，类型 {type}，来自 {endpoint}"),
            ["log.sync.failed"] = (
                "no pude enviar el paquete sync: {err}",
                "could not send the sync packet: {err}",
                "无法发送同步包：{err}"),
            ["log.sync.sent"] = (
                "sync #{count} → {endpoint} rtp={rtp} ntp={ntp}",
                "sync #{count} → {endpoint} rtp={rtp} ntp={ntp}",
                "同步包 #{count} → {endpoint} rtp={rtp} ntp={ntp}"),
            ["log.feedback.sent"] = (
                "feedback → {status} {reason}",
                "feedback → {status} {reason}",
                "心跳 feedback → {status} {reason}"),
            ["log.feedback.failed"] = (
                "feedback falló: {err}",
                "feedback failed: {err}",
                "心跳发送失败：{err}"),
            ["log.device.info"] = (
                "dispositivo: model={model} srcvers={source} status={status}",
                "device: model={model} srcvers={source} status={status}",
                "设备信息：model={model} srcvers={source} status={status}"),
            ["log.info.no_body"] = (
                "GET /info → {status} (sin cuerpo plist)",
                "GET /info → {status} (no plist body)",
                "GET /info → {status}（没有 plist 内容）"),
            ["log.plist.unrecognised"] = (
                "respuesta plist no reconocida: {err}",
                "unrecognised plist response: {err}",
                "无法解析 plist 响应：{err}"),
            ["log.plist.empty"] = (
                "{what}: (sin cuerpo plist)",
                "{what}: (no plist body)",
                "{what}：（没有 plist 内容）"),
            ["log.plist.entry"] = (
                "  {what}: {key} = {value}",
                "  {what}: {key} = {value}",
                "  {what}：{key} = {value}"),

            // ---- Volume ----------------------------------------------------------------
            ["log.volume.kept"] = (
                "se mantiene el volumen del receptor ({volume})",
                "keeping the receiver volume ({volume})",
                "沿用接收端音量（{volume}）"),
            ["log.volume.set_failed"] = (
                "SET_PARAMETER volume → {status}",
                "SET_PARAMETER volume → {status}",
                "SET_PARAMETER volume → {status}"),
            ["log.volume.read"] = (
                "volumen del receptor leído: {volume} ({body})",
                "receiver volume read back: {volume} ({body})",
                "读回接收端音量：{volume}（{body}）"),
            ["log.volume.query"] = (
                "consulta de volumen ({contentType}, uri '{uri}') → {status}: {body}",
                "volume query ({contentType}, uri '{uri}') → {status}: {body}",
                "查询音量（{contentType}，uri '{uri}'）→ {status}：{body}"),
            ["log.volume.query_failed"] = (
                "consulta de volumen falló ({contentType}): {err}",
                "volume query failed ({contentType}): {err}",
                "查询音量失败（{contentType}）：{err}"),
            ["log.volume.adjust_failed"] = (
                "no pude ajustar el volumen: {err}",
                "could not adjust the volume: {err}",
                "无法调整音量：{err}"),

            // ---- Capture ---------------------------------------------------------------
            ["log.capture.enumerate_failed"] = (
                "no pude enumerar los dispositivos de audio: {err}",
                "could not enumerate the audio devices: {err}",
                "无法枚举音频设备：{err}"),
            ["log.capture.default_read_failed"] = (
                "no pude leer la salida predeterminada: {err}",
                "could not read the default output device: {err}",
                "无法读取默认输出设备：{err}"),
            ["log.capture.default_changed"] = (
                "la salida de audio predeterminada del sistema cambió a {device}",
                "the Windows default output device changed to {device}",
                "系统默认输出设备已切换为 {device}"),
            ["log.capture.none"] = (
                "(ninguna)",
                "(none)",
                "（无）"),
            ["log.capture.started"] = (
                "captura WASAPI iniciada: {device} ({format})",
                "WASAPI capture started: {device} ({format})",
                "WASAPI 采集已开始：{device}（{format}）"),
            ["log.capture.rebuilt"] = (
                "captura WASAPI reconstruida: {device}",
                "WASAPI capture rebuilt: {device}",
                "WASAPI 采集已重建：{device}"),
            ["log.capture.default_device"] = (
                "salida predeterminada del sistema",
                "system default output",
                "系统默认输出"),
            ["log.capture.rebuild_failed"] = (
                "no pude reconstruir la captura WASAPI",
                "could not rebuild the WASAPI capture",
                "无法重建 WASAPI 采集"),
            ["log.capture.loop_failed"] = (
                "captura WASAPI: bucle terminado con error",
                "WASAPI capture: loop ended with an error",
                "WASAPI 采集：循环因错误结束"),

            // ---- Pump / audio sending --------------------------------------------------
            ["log.pump.started"] = (
                "bombeador de audio iniciado (captura → ALAC → RTP)",
                "audio pump started (capture → ALAC → RTP)",
                "音频泵已启动（采集 → ALAC → RTP）"),
            ["log.pump.stats"] = (
                "airplay-pump: enviados {sent} paquetes, {queued} en cola, control recibidos {control} (reintentos {retransmits}), NTP recibidos {ntp}",
                "airplay-pump: {sent} packets sent, {queued} queued, {control} control packets received ({retransmits} retransmits), {ntp} NTP requests",
                "airplay-pump：已发送 {sent} 包，队列 {queued}，收到控制包 {control}（重传 {retransmits}），NTP 请求 {ntp}"),
            ["log.pump.failed"] = (
                "airplay-pump: bucle terminado con error",
                "airplay-pump: loop ended with an error",
                "airplay-pump：循环因错误结束"),
            ["log.pump.timeout"] = (
                "el bombeador no terminó a tiempo",
                "the audio pump did not stop in time",
                "音频泵没有及时停止"),
            ["log.pump.stopped"] = (
                "stream detenido (paquetes enviados: {packets})",
                "stream stopped ({packets} packets sent)",
                "推流已停止（已发送 {packets} 包）"),
            ["log.queue.stuck"] = (
                "la cola de audio sigue con {blocks} bloques tras {seconds} s: el envío se queda atrás",
                "the audio queue still holds {blocks} blocks after {seconds} s: sending is falling behind",
                "音频队列在 {seconds} 秒后仍有 {blocks} 块未发出：发送跟不上"),
            ["log.rtp.send_failed"] = (
                "RTP send failed ({dropped} descartados): {err}",
                "RTP send failed ({dropped} dropped): {err}",
                "RTP 发送失败（已丢弃 {dropped} 个包）：{err}"),
            ["log.rtp.retransmit_requested"] = (
                "retransmit solicitado para seq {sequence} (no cacheado)",
                "retransmit requested for seq {sequence} (not cached)",
                "接收端请求重传 seq {sequence}（未缓存）"),
            ["log.rtp.retransmit_sent"] = (
                "retransmit enviado para seq {sequence}",
                "retransmit sent for seq {sequence}",
                "已重传 seq {sequence}"),
            ["log.rtp.retransmit_failed"] = (
                "no pude reenviar el paquete {sequence}: {err}",
                "could not resend packet {sequence}: {err}",
                "无法重传包 {sequence}：{err}"),
            ["log.ntp.socket_error"] = (
                "NTP timing socket error: {err}",
                "NTP timing socket error: {err}",
                "NTP 授时套接字错误：{err}"),
            ["log.ntp.request"] = (
                "NTP timing: {bytes} bytes de {endpoint}, tipo {type}",
                "NTP timing: {bytes} bytes from {endpoint}, type {type}",
                "NTP 授时：来自 {endpoint} 的 {bytes} 字节，类型 {type}"),
            ["log.ntp.discarded"] = (
                "NTP timing: petición descartada ({err})",
                "NTP timing: request discarded ({err})",
                "NTP 授时：请求已丢弃（{err}）"),

            // ---- Playback / test tone --------------------------------------------------
            ["log.playback.started"] = (
                "reproduciendo en {name} ({ip}:{port})",
                "playing on {name} ({ip}:{port})",
                "正在播放到 {name}（{ip}:{port}）"),
            ["log.device.options_ok"] = (
                "connect_device: {name} ({ip}:{port}) respondió a OPTIONS",
                "connect_device: {name} ({ip}:{port}) answered OPTIONS",
                "connect_device：{name}（{ip}:{port}）回应了 OPTIONS"),
            ["log.stream.error"] = (
                "stream error: {err}",
                "stream error: {err}",
                "推流错误：{err}"),
            ["log.stream.close_failed"] = (
                "error cerrando el stream {route}: {err}",
                "error closing stream {route}: {err}",
                "关闭推流 {route} 出错：{err}"),
            ["log.identity.created"] = (
                "identidad de emisor creada: {id}",
                "sender identity created: {id}",
                "已创建发送方身份：{id}"),
            ["log.busy.watchdog"] = (
                "la operación en curso sigue activa tras {minutes} minutos: desbloqueo la interfaz",
                "an operation has been running for {minutes} minutes: unlocking the interface",
                "后台操作已持续 {minutes} 分钟仍未结束：强制解锁界面"),
            ["log.speech.synthesized"] = (
                "voz de prueba sintetizada: {frames} frames @ {rate} Hz",
                "test speech synthesised: {frames} frames @ {rate} Hz",
                "测试语音已合成：{frames} 帧 @ {rate} Hz"),
            ["log.speech.unavailable"] = (
                "síntesis de voz no disponible ({type}): {err}",
                "speech synthesis unavailable ({type}): {err}",
                "语音合成不可用（{type}）：{err}"),
            ["log.speech.no_voice"] = (
                "no hay ninguna voz instalada; se usará la predeterminada del sistema",
                "no voice is installed; the system default will be used",
                "没有安装任何语音包，将使用系统默认语音"),
            ["log.speech.not_16_bit"] = (
                "el WAV sintetizado no es de 16 bits ({bits})",
                "the synthesised WAV is not 16-bit ({bits})",
                "合成出的 WAV 不是 16 位（{bits}）"),
            ["log.speech.unreadable"] = (
                "no pude interpretar el WAV sintetizado",
                "could not parse the synthesised WAV",
                "无法解析合成出的 WAV"),
            ["log.speech.failed"] = (
                "síntesis de voz falló ({type}): {err}",
                "speech synthesis failed ({type}): {err}",
                "语音合成失败（{type}）：{err}"),
            ["log.speech.timeout"] = (
                "la síntesis de voz no terminó a tiempo: se envía el tono de 440 Hz",
                "speech synthesis did not finish in time: sending the 440 Hz tone",
                "语音合成超时：改发 440 Hz 提示音"),
            ["log.tone.sending"] = (
                "enviando prueba de voz: \"{text}\"",
                "sending test speech: \"{text}\"",
                "正在发送测试语音：「{text}」"),
            ["log.tone.no_voice"] = (
                "sin voz disponible: se envía el tono de 440 Hz",
                "no voice available: sending the 440 Hz tone",
                "没有可用语音：改发 440 Hz 提示音"),
            ["log.tone.failed"] = (
                "prueba de conexión falló",
                "the connection test failed",
                "连接测试失败"),

            // ---- Updates ---------------------------------------------------------------
            ["log.update.up_to_date"] = (
                "actualizaciones: {current} sigue siendo la última versión",
                "updates: {current} is still the latest version",
                "更新检查：{current} 仍是最新版本"),
            ["log.update.available"] = (
                "actualizaciones: disponible {version} (instalada {current})",
                "updates: {version} is available (installed {current})",
                "更新检查：发现 {version}（当前 {current}）"),
            ["log.update.check_failed"] = (
                "no pude comprobar si hay actualizaciones: {err}",
                "could not check for updates: {err}",
                "检查更新失败：{err}"),
            ["log.update.dialog_failed"] = (
                "no pude mostrar el diálogo de actualización: {err}",
                "could not show the update dialog: {err}",
                "无法显示更新对话框：{err}"),
            ["log.update.package_chosen"] = (
                "actualización: paquete elegido {package} para {build}",
                "update: package {package} chosen for {build}",
                "更新：本机 {build}，选中安装包 {package}"),
            ["log.update.download_interrupted"] = (
                "descarga de {package} interrumpida ({err}); reintentando desde {mb} MB",
                "download of {package} interrupted ({err}); resuming from {mb} MB",
                "下载 {package} 被中断（{err}）；从 {mb} MB 处续传"),
            ["log.update.staged"] = (
                "actualización {version} descargada y preparada en {path}",
                "update {version} downloaded and staged in {path}",
                "更新 {version} 已下载并准备好，位于 {path}"),
            ["log.update.applying"] = (
                "actualización en curso: AirSend se cierra para reemplazar los archivos",
                "update in progress: AirSend is closing so the files can be replaced",
                "正在安装更新：AirSend 即将退出以替换文件"),
            ["log.update.prepare_failed"] = (
                "no pude preparar la actualización",
                "could not prepare the update",
                "无法准备更新"),
        };

    public static string Format(string? language, string key, object? parameters) =>
        MessageTable.Format(language, Table, key, parameters);

    /// <summary>Keys the catalog knows; used by the completeness test.</summary>
    internal static IReadOnlyCollection<string> Keys => Table.Keys;
}
