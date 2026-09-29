using System.Globalization;

namespace AirSend.Services;

public enum AppLanguage
{
    Spanish,
    English,
    Chinese,
}

/// <summary>
/// String table ported verbatim from <c>ui/src/i18n.ts</c>, so both the Tauri and
/// the WinUI build speak the same Spanish and English copy.
/// </summary>
public sealed class Localization
{
    private static readonly Dictionary<string, string> Spanish = new()
    {
        ["subtitle"] = "Envía el audio de Windows a tu HomePod",
        ["scan"] = "Buscar dispositivos",
        ["scan_searching"] = "buscando…",
        ["devices_count_one"] = "1 dispositivo",
        ["devices_count_other"] = "{n} dispositivos",
        ["connect"] = "Conectar",
        ["connecting"] = "Conectando…",
        ["disconnect"] = "Desconectar",
        ["play_generic"] = "Reproducir audio del PC",
        ["play_to"] = "Enviar audio del PC a {name}",
        ["stop"] = "Parar",
        ["player_starting"] = "iniciando…",
        ["player_playing"] = "reproduciendo",
        ["multi_device"] = "Reproducir en varios dispositivos",
        ["multi_device_hint"] = "Experimental: al conectar otro receptor durante la reproducción, se añadirá al audio actual. Los receptores pueden no estar perfectamente sincronizados.",
        ["switch_title"] = "Cambiar dispositivo de audio",
        ["switch_message"] = "El audio se está enviando a {from}. Con varios dispositivos desactivado, conectar {to} cambiará la reproducción y desconectará {from}.",
        ["switch_confirm"] = "Cambiar a {name}",
        ["multi_off_title"] = "Desactivar varios dispositivos",
        ["multi_off_message"] = "Se detendrá la reproducción en los demás dispositivos y continuará en {name}.",
        ["multi_off_confirm"] = "Continuar solo en {name}",
        ["cancel"] = "Cancelar",
        ["volume"] = "Volumen",
        ["latency"] = "Límite de búfer solicitado",
        ["latency_lower"] = "Menos retardo",
        ["latency_safer"] = "Más estabilidad",
        ["latency_hint"] = "Confirma el cambio para aplicarlo. Solo puedes cambiarlo una vez cada 10 segundos. Si se está reproduciendo audio, la conexión se reinicia brevemente. El mínimo es 100 ms: por debajo, el receptor acepta la sesión pero se queda en silencio. El receptor puede añadir más retardo.",
        ["latency_confirm"] = "Confirmar",
        ["latency_current"] = "Valor aplicado",
        ["latency_pending"] = "Pendiente de confirmar",
        ["latency_applying"] = "Aplicando…",
        ["latency_cooldown"] = "Disponible en {seconds} s",
        ["latency_error"] = "No se pudo aplicar la latencia: {err}",
        ["capture_interrupted"] = "La captura de audio se interrumpió (dispositivo desconectado o fallo del controlador)",
        ["manual_summary"] = "¿No aparece tu HomePod? Añade su IP y puerto manualmente",
        ["manual_hint"] = "Puedes usar una IP o IP:puerto, por ejemplo 192.168.1.50:7453. Para IPv6 con puerto usa [dirección]:puerto.",
        ["manual_endpoint_placeholder"] = "192.168.1.50[:7000]",
        ["manual_name_placeholder"] = "Nombre (opcional)",
        ["manual_add"] = "Añadir",
        ["manual_checking"] = "verificando…",
        ["manual_ok"] = "OK: {name}",
        ["manual_need_ip"] = "introduce una IP",
        ["reconnecting"] = "reconectando a {name}…",
        ["cant_find"] = "no encuentro {name}: {err}",
        ["error_prefix"] = "error: {err}",
        ["vol_error_prefix"] = "vol err: {err}",
        ["lang_toggle_to_en"] = "EN",
        ["lang_toggle_to_es"] = "ES",
        ["lang_toggle_title"] = "Cambiar idioma",
        ["log_summary"] = "Registro de actividad",
        ["capture_source"] = "Fuente de audio a capturar",
        ["capture_source_hint"] = "Por defecto se captura la salida del sistema. Para que el receptor aparezca en la lista de altavoces de Windows, cambia la salida a un cable virtual (por ejemplo VB-CABLE) y selecciónalo aquí.",
        ["capture_follow_default"] = "Seguir la salida predeterminada del sistema",
        ["test_tone"] = "Probar sonido (440 Hz)",
        ["tone_playing"] = "enviando tono…",
        ["auto_connect"] = "Conectar automáticamente al arrancar",
        ["auto_connect_hint"] = "Al abrir la aplicación se conecta y empieza a enviar audio al dispositivo elegido.",
        ["auto_connect_last"] = "Último dispositivo utilizado",
        ["tab_devices"] = "Dispositivos",
        ["tab_playback"] = "Reproducción",
        ["tab_settings"] = "Ajustes",
        ["devices_title"] = "Dispositivos",
        ["devices_description"] = "Busca receptores AirPlay 2 en tu red local y conéctate a uno de ellos.",
        ["devices_empty_title"] = "Aún no hay dispositivos",
        ["devices_empty_description"] = "Comprueba que el receptor está en la misma red y pulsa Buscar dispositivos. Si mDNS no funciona en tu router, añade su IP manualmente.",
        ["playback_title"] = "Reproducción",
        ["playback_description"] = "Volumen, prueba de conexión y límite de búfer del dispositivo conectado.",
        ["playback_current_device"] = "Dispositivo actual",
        ["playback_no_device"] = "Sin dispositivo",
        ["playback_no_device_description"] = "Conecta un receptor en la pestaña Dispositivos para enviar el audio del PC.",
        ["playback_go_to_devices"] = "Ir a Dispositivos",
        ["playback_tone"] = "Prueba de conexión",
        ["playback_tone_description"] = "El PC sintetiza una frase en el idioma de la interfaz y la envía al receptor para comprobar la ruta de audio.",
        ["playback_tone_needs_playback"] = "Si no está reproduciendo, se inicia primero y luego se envía la frase.",
        ["playback_tone_action"] = "Enviar frase de prueba",
        ["test_speech_text"] = "Esta es una prueba de conexión de AirSend.",
        ["retry"] = "Reintentar",
        ["settings_language"] = "Idioma de la interfaz",
        ["settings_logs"] = "Registros",
        ["log_show"] = "Mostrar registros",
        ["log_hide"] = "Ocultar registros",
        ["settings_open_logs"] = "Abrir carpeta de registros",
        ["settings_open_settings"] = "Abrir carpeta de datos",
        ["settings_logs_hint"] = "La interfaz sigue el idioma del sistema la primera vez. Los registros se guardan por día.",
        ["settings_about"] = "Acerca de",
        ["settings_about_text"] = "AirSend — envía el audio de Windows a un HomePod por AirPlay 2. Reescritura en WinUI 3 / C# / .NET 10 del proyecto original en Rust + Tauri. Licencia GPL-3.0-or-later.",
        ["settings_version"] = "Versión {version}",
        ["settings_group_general"] = "General",
        ["settings_group_audio"] = "Audio",
        ["settings_group_logs"] = "Registros y diagnóstico",
        ["settings_group_about"] = "Acerca de",
        ["settings_language_desc"] = "La primera vez sigue el idioma del sistema.",
        ["settings_auto_connect_device"] = "Dispositivo al que conectarse",
        ["settings_start_with_windows"] = "Iniciar con Windows",
        ["settings_start_with_windows_desc"] = "Arranca minimizado en la bandeja al iniciar sesión; combinado con la conexión automática, empieza a enviar audio solo.",
        ["settings_startup_failed"] = "No se pudo cambiar el inicio automático: {err}",
        ["no_device_hint"] = "Conecta un dispositivo en la pestaña Dispositivos para empezar a enviar audio.",
        ["connect_ready_title"] = "Conectado a {name}",
        ["connect_ready_message"] = "¿Quieres empezar a enviar el audio del PC a {name}?",
        ["connect_ready_accept"] = "Empezar a enviar",
        ["later"] = "Más tarde",
        ["loud_title"] = "El volumen puede ser demasiado alto",
        ["loud_message"] = "Has subido el volumen por encima del {threshold}%. A ese nivel el sonido del receptor puede ser muy fuerte y la exposición prolongada daña la audición. ¿Quieres seguir subiéndolo?",
        ["loud_accept"] = "Seguir subiendo",
        ["loud_keep"] = "Mantener {volume}%",
        ["quiet_title"] = "Volumen muy bajo",
        ["quiet_message"] = "Estás por debajo del {threshold}%: puede que casi no se oiga en el receptor.",
        ["loud_dont_ask"] = "No volver a avisar en esta sesión",
        ["tray_show"] = "Mostrar / ocultar ventana",
        ["tray_quit"] = "Salir",
        ["app_name"] = "AirSend",
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["subtitle"] = "Send your Windows audio to your HomePod",
        ["scan"] = "Scan devices",
        ["scan_searching"] = "scanning…",
        ["devices_count_one"] = "1 device",
        ["devices_count_other"] = "{n} devices",
        ["connect"] = "Connect",
        ["connecting"] = "Connecting…",
        ["disconnect"] = "Disconnect",
        ["play_generic"] = "Play PC audio",
        ["play_to"] = "Send PC audio to {name}",
        ["stop"] = "Stop",
        ["player_starting"] = "starting…",
        ["player_playing"] = "playing",
        ["multi_device"] = "Play on multiple devices",
        ["multi_device_hint"] = "Experimental: connecting another receiver during playback adds it to the current audio. Receivers may not be perfectly synchronized.",
        ["switch_title"] = "Switch audio device",
        ["switch_message"] = "Audio is playing on {from}. With multiple devices off, connecting {to} will switch playback and disconnect {from}.",
        ["switch_confirm"] = "Switch to {name}",
        ["multi_off_title"] = "Turn off multiple devices",
        ["multi_off_message"] = "Playback will stop on the other devices and continue on {name}.",
        ["multi_off_confirm"] = "Keep only {name}",
        ["cancel"] = "Cancel",
        ["volume"] = "Volume",
        ["latency"] = "Requested buffer limit",
        ["latency_lower"] = "Less delay",
        ["latency_safer"] = "More stable",
        ["latency_hint"] = "Confirm to apply a change. You can change it only once every 10 seconds. If audio is playing, the connection briefly restarts. 100 ms is the minimum: below that the receiver accepts the session but stays silent. The receiver may add more delay.",
        ["latency_confirm"] = "Confirm",
        ["latency_current"] = "Applied value",
        ["latency_pending"] = "Awaiting confirmation",
        ["latency_applying"] = "Applying…",
        ["latency_cooldown"] = "Available in {seconds} s",
        ["latency_error"] = "Could not apply latency: {err}",
        ["capture_interrupted"] = "Audio capture stopped (device disconnected or driver failed)",
        ["manual_summary"] = "Can't see your HomePod? Add its IP and port manually",
        ["manual_hint"] = "Enter an IP or IP:port, for example 192.168.1.50:7453. Use [address]:port for IPv6.",
        ["manual_endpoint_placeholder"] = "192.168.1.50[:7000]",
        ["manual_name_placeholder"] = "Name (optional)",
        ["manual_add"] = "Add",
        ["manual_checking"] = "checking…",
        ["manual_ok"] = "OK: {name}",
        ["manual_need_ip"] = "enter an IP",
        ["reconnecting"] = "reconnecting to {name}…",
        ["cant_find"] = "can't find {name}: {err}",
        ["error_prefix"] = "error: {err}",
        ["vol_error_prefix"] = "vol err: {err}",
        ["lang_toggle_to_en"] = "EN",
        ["lang_toggle_to_es"] = "ES",
        ["lang_toggle_title"] = "Change language",
        ["log_summary"] = "Activity log",
        ["capture_source"] = "Audio source to capture",
        ["capture_source_hint"] = "By default the system output is captured. To make the receiver show up in the Windows speaker list, switch the output to a virtual cable (for example VB-CABLE) and pick it here.",
        ["capture_follow_default"] = "Follow the system default output",
        ["test_tone"] = "Test sound (440 Hz)",
        ["tone_playing"] = "sending tone…",
        ["auto_connect"] = "Connect automatically on startup",
        ["auto_connect_hint"] = "When the app opens, connect and start sending audio to the selected device.",
        ["auto_connect_last"] = "Last device used",
        ["tab_devices"] = "Devices",
        ["tab_playback"] = "Playback",
        ["tab_settings"] = "Settings",
        ["devices_title"] = "Devices",
        ["devices_description"] = "Find AirPlay 2 receivers on your local network and connect to one of them.",
        ["devices_empty_title"] = "No devices yet",
        ["devices_empty_description"] = "Make sure the receiver is on the same network and press Scan devices. If mDNS does not work on your router, add its IP manually.",
        ["playback_title"] = "Playback",
        ["playback_description"] = "Volume, connection test and buffer limit for the connected device.",
        ["playback_current_device"] = "Current device",
        ["playback_no_device"] = "No device",
        ["playback_no_device_description"] = "Connect a receiver on the Devices tab to send your PC audio.",
        ["playback_go_to_devices"] = "Go to Devices",
        ["playback_tone"] = "Connection test",
        ["playback_tone_description"] = "The PC speaks a short sentence in the interface language and sends it to the receiver to check the audio path.",
        ["playback_tone_needs_playback"] = "If nothing is playing, playback starts first and then the sentence is sent.",
        ["playback_tone_action"] = "Send test sentence",
        ["test_speech_text"] = "This is an AirSend connection test.",
        ["retry"] = "Retry",
        ["settings_language"] = "Interface language",
        ["settings_logs"] = "Logs",
        ["log_show"] = "Show log",
        ["log_hide"] = "Hide log",
        ["settings_open_logs"] = "Open log folder",
        ["settings_open_settings"] = "Open data folder",
        ["settings_logs_hint"] = "The interface follows the system language on first run. Logs are rotated daily.",
        ["settings_about"] = "About",
        ["settings_about_text"] = "AirSend — send your Windows audio to a HomePod over AirPlay 2. WinUI 3 / C# / .NET 10 rewrite of the original Rust + Tauri project. GPL-3.0-or-later.",
        ["settings_version"] = "Version {version}",
        ["settings_group_general"] = "General",
        ["settings_group_audio"] = "Audio",
        ["settings_group_logs"] = "Logs & diagnostics",
        ["settings_group_about"] = "About",
        ["settings_language_desc"] = "Follows the system language on first run.",
        ["settings_auto_connect_device"] = "Device to connect to",
        ["settings_start_with_windows"] = "Start with Windows",
        ["settings_start_with_windows_desc"] = "Starts minimized in the tray at sign-in; with auto-connect enabled it begins sending audio on its own.",
        ["settings_startup_failed"] = "Could not change the startup setting: {err}",
        ["no_device_hint"] = "Connect a device on the Devices tab to start sending audio.",
        ["connect_ready_title"] = "Connected to {name}",
        ["connect_ready_message"] = "Do you want to start sending your PC audio to {name}?",
        ["connect_ready_accept"] = "Start sending",
        ["later"] = "Later",
        ["loud_title"] = "Volume may be too loud",
        ["loud_message"] = "You raised the volume above {threshold}%. At that level the receiver can get very loud, and prolonged exposure damages your hearing. Do you want to keep going?",
        ["loud_accept"] = "Keep raising",
        ["loud_keep"] = "Keep {volume}%",
        ["quiet_title"] = "Very low volume",
        ["quiet_message"] = "You are below {threshold}%: you may barely hear it on the receiver.",
        ["loud_dont_ask"] = "Don't remind me again in this session",
        ["tray_show"] = "Show / hide window",
        ["tray_quit"] = "Quit",
        ["app_name"] = "AirSend",
    };

    private static readonly Dictionary<string, string> Chinese = new()
    {
        ["subtitle"] = "把 Windows 的声音发送到你的 HomePod",
        ["scan"] = "搜索设备",
        ["scan_searching"] = "搜索中…",
        ["devices_count_one"] = "1 台设备",
        ["devices_count_other"] = "{n} 台设备",
        ["connect"] = "连接",
        ["connecting"] = "连接中…",
        ["disconnect"] = "断开",
        ["play_generic"] = "播放电脑声音",
        ["play_to"] = "把电脑声音发送到 {name}",
        ["stop"] = "停止",
        ["player_starting"] = "启动中…",
        ["player_playing"] = "播放中",
        ["multi_device"] = "同时播放到多台设备",
        ["multi_device_hint"] = "实验性功能：播放过程中连接另一台接收器，会把它加到当前音频里。多台接收器之间可能无法完全同步。",
        ["switch_title"] = "切换音频设备",
        ["switch_message"] = "当前正在把音频发送到 {from}。未开启多设备时，连接 {to} 会切换播放并断开 {from}。",
        ["switch_confirm"] = "切换到 {name}",
        ["multi_off_title"] = "关闭多设备播放",
        ["multi_off_message"] = "将停止在其他设备上的播放，只保留 {name}。",
        ["multi_off_confirm"] = "只在 {name} 上播放",
        ["cancel"] = "取消",
        ["volume"] = "音量",
        ["latency"] = "请求的缓冲区上限",
        ["latency_lower"] = "延迟更低",
        ["latency_safer"] = "更稳定",
        ["latency_hint"] = "需要点击“确认”才会生效，而且每 10 秒只能修改一次。正在播放时修改会短暂重连。最小值为 100 ms：低于它接收端会接受会话但不出声。接收端仍可能自行增加延迟。",
        ["latency_confirm"] = "确认",
        ["latency_current"] = "当前生效值",
        ["latency_pending"] = "待确认",
        ["latency_applying"] = "正在应用…",
        ["latency_cooldown"] = "{seconds} 秒后可用",
        ["latency_error"] = "无法应用该延迟：{err}",
        ["capture_interrupted"] = "系统音频采集已中断（设备被拔出或驱动异常）",
        ["manual_summary"] = "搜不到 HomePod？手动添加它的 IP 和端口",
        ["manual_hint"] = "可以填写 IP 或 IP:端口，例如 192.168.1.50:7453。IPv6 带端口请写成 [地址]:端口。",
        ["manual_endpoint_placeholder"] = "192.168.1.50[:7000]",
        ["manual_name_placeholder"] = "名称（可选）",
        ["manual_add"] = "添加",
        ["manual_checking"] = "正在校验…",
        ["manual_ok"] = "已添加：{name}",
        ["manual_need_ip"] = "请输入 IP",
        ["reconnecting"] = "正在重新连接 {name}…",
        ["cant_find"] = "找不到 {name}：{err}",
        ["error_prefix"] = "错误：{err}",
        ["vol_error_prefix"] = "音量错误：{err}",
        ["lang_toggle_to_en"] = "EN",
        ["lang_toggle_to_es"] = "ES",
        ["lang_toggle_title"] = "切换语言",
        ["log_summary"] = "运行日志",
        ["capture_source"] = "采集的音频来源",
        ["capture_source_hint"] = "默认采集系统当前输出。如果希望接收器出现在 Windows 的扬声器列表里，可以先把系统输出切到虚拟声卡（例如 VB-CABLE），然后在这里选择该声卡。",
        ["capture_follow_default"] = "跟随系统默认输出",
        ["test_tone"] = "播放测试音（440 Hz）",
        ["tone_playing"] = "正在发送测试音…",
        ["auto_connect"] = "启动时自动连接",
        ["auto_connect_hint"] = "打开应用后自动连接并开始把声音发送到所选设备。",
        ["auto_connect_last"] = "上次使用的设备",
        ["tab_devices"] = "设备",
        ["tab_playback"] = "播放",
        ["tab_settings"] = "设置",
        ["devices_title"] = "设备",
        ["devices_description"] = "搜索局域网里的 AirPlay 2 接收器，然后连接其中一台。",
        ["devices_empty_title"] = "还没有发现设备",
        ["devices_empty_description"] = "确认接收器和电脑在同一网络后点击「搜索设备」；如果路由器不转发 mDNS，可以手动添加它的 IP。",
        ["playback_title"] = "播放",
        ["playback_description"] = "当前连接设备的音量、连接测试与缓冲区上限。",
        ["playback_current_device"] = "当前设备",
        ["playback_no_device"] = "未连接设备",
        ["playback_no_device_description"] = "请先到「设备」页连接一台接收器，然后就能把电脑声音发送过去。",
        ["playback_go_to_devices"] = "前往「设备」页",
        ["playback_tone"] = "连接测试",
        ["playback_tone_description"] = "由电脑用界面语言合成一句话发给接收器，用来确认音频通路是否正常。",
        ["playback_tone_needs_playback"] = "若未在播放，会先开始播放，再发送测试语音。",
        ["playback_tone_action"] = "发送测试语音",
        ["test_speech_text"] = "这是 AirSend 的连接测试。",
        ["retry"] = "重试",
        ["settings_language"] = "界面语言",
        ["settings_logs"] = "日志",
        ["log_show"] = "显示日志",
        ["log_hide"] = "隐藏日志",
        ["settings_open_logs"] = "打开日志文件夹",
        ["settings_open_settings"] = "打开数据文件夹",
        ["settings_logs_hint"] = "首次启动时跟随系统语言；日志按天轮转保存。",
        ["settings_about"] = "关于",
        ["settings_about_text"] = "AirSend —— 通过 AirPlay 2 把 Windows 的声音发送到 HomePod。原项目为 Rust + Tauri，这里是 WinUI 3 / C# / .NET 10 重构版，许可证 GPL-3.0-or-later。",
        ["settings_version"] = "版本 {version}",
        ["settings_group_general"] = "常规",
        ["settings_group_audio"] = "音频",
        ["settings_group_logs"] = "日志与诊断",
        ["settings_group_about"] = "关于",
        ["settings_language_desc"] = "首次启动时跟随系统语言。",
        ["settings_auto_connect_device"] = "自动连接的设备",
        ["settings_start_with_windows"] = "开机时自动启动",
        ["settings_start_with_windows_desc"] = "登录后在托盘里静默启动；配合“启动时自动连接”，可以自动开始推送音频。",
        ["settings_startup_failed"] = "无法修改开机启动设置：{err}",
        ["no_device_hint"] = "请先在「设备」页连接一台设备，然后就能开始发送声音。",
        ["connect_ready_title"] = "已连接到 {name}",
        ["connect_ready_message"] = "要现在开始把电脑声音发送到 {name} 吗？",
        ["connect_ready_accept"] = "开始发送",
        ["later"] = "稍后",
        ["loud_title"] = "音量可能过大",
        ["loud_message"] = "你已经把音量提高到 {threshold}% 以上。这个音量下接收器可能非常响，长时间大音量会损伤听力。确认继续提高吗？",
        ["loud_accept"] = "继续提高",
        ["loud_keep"] = "保持 {volume}%",
        ["quiet_title"] = "音量过低",
        ["quiet_message"] = "当前低于 {threshold}%，接收器上可能几乎听不见。",
        ["loud_dont_ask"] = "本次启动不再提醒",
        ["tray_show"] = "显示 / 隐藏窗口",
        ["tray_quit"] = "退出",
        ["app_name"] = "AirSend",
    };

    private readonly SettingsStore _settings;

    public Localization(SettingsStore settings)
    {
        _settings = settings;
        Language = Detect();
    }

    public AppLanguage Language { get; private set; }

    public event Action? LanguageChanged;

    public string this[string key] => Translate(key, null);

    public bool IsSpanish => Language == AppLanguage.Spanish;

    /// <summary>Language tag stored in settings, e.g. "zh".</summary>
    public string Tag => Language switch
    {
        AppLanguage.Spanish => "es",
        AppLanguage.Chinese => "zh",
        _ => "en",
    };

    public static IReadOnlyList<(string Tag, string NativeName)> Options { get; } =
    [
        ("zh", "中文"),
        ("en", "English"),
        ("es", "Español"),
    ];

    public void Toggle()
    {
        SetLanguage(Language == AppLanguage.Spanish ? AppLanguage.English : AppLanguage.Spanish);
    }

    public void SetLanguage(AppLanguage language)
    {
        if (language == Language)
        {
            return;
        }

        Language = language;
        _settings.Language = Tag;
        LanguageChanged?.Invoke();
    }

    /// <summary>Sets the language from a settings tag ("zh" / "en" / "es").</summary>
    public void SetLanguage(string? tag)
    {
        switch (tag?.ToLowerInvariant())
        {
            case "zh":
                SetLanguage(AppLanguage.Chinese);
                break;
            case "es":
                SetLanguage(AppLanguage.Spanish);
                break;
            case "en":
                SetLanguage(AppLanguage.English);
                break;
        }
    }

    public string T(string key, object? parameters = null) => Translate(key, parameters);

    private string Translate(string key, object? parameters)
    {
        Dictionary<string, string> dictionary = Language switch
        {
            AppLanguage.Spanish => Spanish,
            AppLanguage.Chinese => Chinese,
            _ => English,
        };
        if (!dictionary.TryGetValue(key, out string? value))
        {
            value = Spanish.TryGetValue(key, out string? fallback) ? fallback : key;
        }

        if (parameters is not null)
        {
            foreach (System.Reflection.PropertyInfo property in parameters.GetType().GetProperties())
            {
                value = value.Replace(
                    $"{{{property.Name}}}",
                    Convert.ToString(property.GetValue(parameters), CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        return value;
    }

    private AppLanguage Detect()
    {
        string? saved = _settings.Language;
        switch (saved?.ToLowerInvariant())
        {
            case "es":
                return AppLanguage.Spanish;
            case "en":
                return AppLanguage.English;
            case "zh":
                return AppLanguage.Chinese;
        }

        // No saved choice: follow the Windows display language.
        string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return system.ToLowerInvariant() switch
        {
            "es" => AppLanguage.Spanish,
            "zh" => AppLanguage.Chinese,
            _ => AppLanguage.English,
        };
    }
}
