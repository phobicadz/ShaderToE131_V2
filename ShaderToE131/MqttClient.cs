using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace ShaderToE131;

/// <summary>
/// MQTT front-end for the same control surface the HTTP API exposes, plus Home Assistant
/// MQTT discovery so the matrix appears as a native <c>light</c> entity whose <i>effects</i>
/// are the shaders.
///
/// Topics (default prefix <c>shadertoe131</c>):
///   <c>homeassistant/light/&lt;prefix&gt;_matrix/config</c>  retained discovery payload
///   <c>&lt;prefix&gt;/matrix/set</c>           commands: {"state":"ON|OFF","effect":"&lt;shader&gt;"} or bare ON/OFF
///   <c>&lt;prefix&gt;/matrix/state</c>         retained {"state":"ON|OFF","effect":"&lt;shader&gt;"}
///   <c>&lt;prefix&gt;/matrix/attributes</c>    live stats (frames sent, errors, audio, string config)
///   <c>&lt;prefix&gt;/matrix/availability</c>  retained online/offline (with a last-will)
///   <c>&lt;prefix&gt;/notify/set</c>           banner text (plain text or {"text":…,"duration":…})
///   <c>&lt;prefix&gt;/notify/state</c>          retained banner currently on screen ("" when none)
///   <c>&lt;prefix&gt;/notify/duration/set</c>   banner lifetime in seconds (from the number entity)
///   <c>&lt;prefix&gt;/notify/duration/state</c> retained current banner lifetime
///
/// A second discovered entity, <c>select.&lt;prefix&gt;_shader</c>, exposes the shader list as a
/// Home Assistant select entity (options = off / built-in default / every shader name), which is
/// how the effects are driven reliably: this HA build does not surface the light's
/// <c>effect_list</c>, but select entities map 1:1 onto the shader names.
///
/// Text banners have their own pair of entities: <c>text.&lt;prefix&gt;_message</c> (type something,
/// it appears on the matrix; its state mirrors the banner currently showing) and
/// <c>number.&lt;prefix&gt;_banner_seconds</c> (how long a plain-text banner stays up).
///
/// Commands are routed through <see cref="WebServer.SelectShaderByName"/> and
/// <see cref="WebServer.TryPostNotification"/>, i.e. the identical validation the HTTP
/// endpoints use — MQTT never bypasses or duplicates those rules.
/// </summary>
public sealed class MqttClient : IDisposable
{
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly WebServer _web;
    private readonly string _host;
    private readonly int _port;
    private readonly string _prefix;
    private readonly string _uniqueId;

    private System.Threading.Timer? _heartbeat;
    private volatile string _lastEffect = "";
    private volatile bool _disposed;

    private const int ReconnectDelayMs = 10_000;
    private const int HeartbeatMs = 30_000;

    public string SetTopic => $"{_prefix}/matrix/set";
    public string StateTopic => $"{_prefix}/matrix/state";
    public string AttributesTopic => $"{_prefix}/matrix/attributes";
    public string AvailabilityTopic => $"{_prefix}/matrix/availability";
    public string NotifySetTopic => $"{_prefix}/notify/set";
    public string DiscoveryTopic => $"homeassistant/light/{_uniqueId}/config";
    public string ShaderSelectCommandTopic => $"{_prefix}/shader/set";
    public string ShaderSelectStateTopic => $"{_prefix}/shader/state";
    public string ShaderSelectDiscoveryTopic => $"homeassistant/select/{_prefix}_shader/config";
    public string NotifyStateTopic => $"{_prefix}/notify/state";
    public string NotifyDurationSetTopic => $"{_prefix}/notify/duration/set";
    public string NotifyDurationStateTopic => $"{_prefix}/notify/duration/state";
    public string NotifyDiscoveryTopic => $"homeassistant/text/{_prefix}_message/config";
    public string NotifyDurationDiscoveryTopic => $"homeassistant/number/{_prefix}_banner_seconds/config";

    /// <summary>Banner lifetime used for plain-text commands, set by the banner-seconds entity.</summary>
    private volatile int _notifyDurationSec = DefaultNotifyDurationSec;
    private const int DefaultNotifyDurationSec = 10;
    public const int MinNotifyDurationSec = 1;
    public const int MaxNotifyDurationSec = 300;

    public MqttClient(string host, int port, string topicPrefix, WebServer web)
    {
        _host = host;
        _port = port;
        _prefix = topicPrefix;
        _uniqueId = $"{topicPrefix}_matrix";
        _web = web;
        _client = _factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += _ =>
        {
            if (!_disposed) Console.WriteLine("[Mqtt] disconnected; retrying.");
            return Task.CompletedTask;
        };
    }

    /// <summary>Start the connect/reconnect loop. Returns immediately; failures are retried forever.</summary>
    public void Start()
    {
        _ = Task.Run(RunAsync);
        _heartbeat = new System.Threading.Timer(_ => PublishState(), null, HeartbeatMs, HeartbeatMs);
    }

    private async Task RunAsync()
    {
        while (!_disposed)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(_host, _port)
                        .WithClientId($"{_prefix}-{Environment.MachineName.ToLowerInvariant()}")
                        .WithCleanSession(false)
                        .WithProtocolVersion(MqttProtocolVersion.V500)
                        .WithWillTopic(AvailabilityTopic)
                        .WithWillPayload("offline")
                        .WithWillRetain()
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _client.ConnectAsync(options, CancellationToken.None);
                    Console.WriteLine($"[Mqtt] connected to {_host}:{_port} (client id {_prefix}).");

                    await SubscribeAsync();
                    await PublishAsync(AvailabilityTopic, "online", retain: true);
                    await PublishDiscoveryAsync();
                    await PublishShaderSelectDiscoveryAsync();
                    await PublishBannerEntitiesDiscoveryAsync();
                    await PublishStateAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mqtt] broker error ({_host}:{_port}): {ex.Message}");
                // Drop the half-initialised session so the next pass re-connects and re-publishes
                // discovery from scratch instead of sitting on a connected-but-uninitialised client.
                try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { /* retried anyway */ }
                await Task.Delay(ReconnectDelayMs);
            }

            await Task.Delay(5_000);
        }
    }

    private async Task SubscribeAsync()
    {
        await _client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(SetTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build());
        await _client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(NotifySetTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build());
        await _client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(ShaderSelectCommandTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build());
        await _client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(NotifyDurationSetTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build());
        Console.WriteLine($"[Mqtt] subscribed to {SetTopic}, {NotifySetTopic}, {ShaderSelectCommandTopic}, {NotifyDurationSetTopic}.");
    }

    // ─── Home Assistant discovery ───

    private Task PublishDiscoveryAsync()
    {
        string[] effects = _web.Shaders.Select(s => s.Name).ToArray();

        var discovery = new Dictionary<string, object>
        {
            ["name"] = "LED Matrix",
            ["unique_id"] = _uniqueId,
            ["schema"] = "json",
            ["command_topic"] = SetTopic,
            ["state_topic"] = StateTopic,
            ["attribute_topic"] = AttributesTopic,
            ["availability_topic"] = AvailabilityTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["effect_list"] = effects,
            ["effect_value_template"] = "{{ value_json.effect }}",
            ["state_value_template"] = "{{ value_json.state }}",
            ["qos"] = 1,
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { _prefix },
                ["name"] = "ShaderToE131",
                ["manufacturer"] = "phobicadz",
                ["model"] = "ShaderToE131 E1.31 LED matrix",
            },
        };

        Console.WriteLine($"[Mqtt] publishing Home Assistant discovery for light '{_uniqueId}' ({effects.Length} effects).");
        return PublishAsync(DiscoveryTopic, JsonSerializer.Serialize(discovery), retain: true);
    }

    /// <summary>
    /// Discovery payload for the companion select entity: one option per shader, plus the two
    /// pseudo-shaders the app understands ("off" and "built-in default").
    /// </summary>
    private Task PublishShaderSelectDiscoveryAsync()
    {
        var options = new List<string> { "off", "built-in default" };
        options.AddRange(_web.Shaders.Select(s => s.Name));

        var discovery = new Dictionary<string, object>
        {
            ["name"] = "Shader",
            ["unique_id"] = $"{_prefix}_shader",
            ["options"] = options,
            ["command_topic"] = ShaderSelectCommandTopic,
            ["state_topic"] = ShaderSelectStateTopic,
            ["availability_topic"] = AvailabilityTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["icon"] = "mdi:shader",
            ["qos"] = 1,
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { _prefix },
                ["name"] = "ShaderToE131",
                ["manufacturer"] = "phobicadz",
                ["model"] = "ShaderToE131 E1.31 LED matrix",
            },
        };

        Console.WriteLine($"[Mqtt] publishing Home Assistant discovery for select '{_prefix}_shader' ({options.Count} options).");
        return PublishAsync(ShaderSelectDiscoveryTopic, JsonSerializer.Serialize(discovery), retain: true);
    }

    private Dictionary<string, object> DeviceBlock() => new()
    {
        ["identifiers"] = new[] { _prefix },
        ["name"] = "ShaderToE131",
        ["manufacturer"] = "phobicadz",
        ["model"] = "ShaderToE131 E1.31 LED matrix",
    };

    /// <summary>
    /// Discovery payloads for the banner entities: a text entity whose commands go straight to the
    /// notification endpoint, and a number entity that sets how long plain-text banners stay lit.
    /// </summary>
    private async Task PublishBannerEntitiesDiscoveryAsync()
    {
        var message = new Dictionary<string, object>
        {
            ["name"] = "Matrix message",
            ["unique_id"] = $"{_prefix}_message",
            ["command_topic"] = NotifySetTopic,
            ["state_topic"] = NotifyStateTopic,
            ["min"] = 0,
            ["max"] = TextRenderer.MaxTextLength,
            ["mode"] = "text",
            ["availability_topic"] = AvailabilityTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["icon"] = "mdi:sign-side",
            ["qos"] = 1,
            ["device"] = DeviceBlock(),
        };
        await PublishAsync(NotifyDiscoveryTopic, JsonSerializer.Serialize(message), retain: true);
        Console.WriteLine($"[Mqtt] publishing Home Assistant discovery for text '{_prefix}_message'.");

        var duration = new Dictionary<string, object>
        {
            ["name"] = "Banner seconds",
            ["unique_id"] = $"{_prefix}_banner_seconds",
            ["command_topic"] = NotifyDurationSetTopic,
            ["state_topic"] = NotifyDurationStateTopic,
            ["min"] = MinNotifyDurationSec,
            ["max"] = MaxNotifyDurationSec,
            ["step"] = 1,
            ["mode"] = "slider",
            ["unit_of_measurement"] = "s",
            ["availability_topic"] = AvailabilityTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["icon"] = "mdi:timer-outline",
            ["qos"] = 1,
            ["device"] = DeviceBlock(),
        };
        await PublishAsync(NotifyDurationDiscoveryTopic, JsonSerializer.Serialize(duration), retain: true);
        Console.WriteLine($"[Mqtt] publishing Home Assistant discovery for number '{_prefix}_banner_seconds'.");
    }

    // ─── Commands ───

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        string topic = e.ApplicationMessage.Topic;
        string payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";

        try
        {
            if (topic == SetTopic)
            {
                if (!TryParseLightCommand(payload, out string? state, out string? effect, out string? error))
                {
                    Console.WriteLine($"[Mqtt] rejected command on {topic}: {error}");
                    return;
                }

                string? target = effect
                    ?? (state == "OFF" ? "off"
                        : state == "ON" && _lastEffect.Length > 0 ? _lastEffect
                        : null);

                if (target == null)
                {
                    // "ON" with no effect and nothing previously selected: the render loop keeps
                    // whatever is already loaded, so there is nothing to change.
                    Console.WriteLine($"[Mqtt] '{state}' with no effect ignored — send an effect name to pick a shader.");
                }
                else
                {
                    string? selected = _web.SelectShaderByName(target, out string? selectError);
                    if (selected == null)
                    {
                        Console.WriteLine($"[Mqtt] {selectError}");
                        return;
                    }
                    if (target != "off") _lastEffect = target;
                    Console.WriteLine($"[Mqtt] light command → {selected}");
                }

                // Let the render loop pick the change up (it applies pending shaders on the next
                // frame and refreshes the status snapshot in the same frame), then publish the
                // authoritative state instead of an optimistic guess.
                await Task.Delay(1500);
                await PublishStateAsync();
            }
            else if (topic == ShaderSelectCommandTopic)
            {
                string name = payload.Trim();
                string? selected = _web.SelectShaderByName(name, out string? selectError);
                if (selected == null)
                {
                    Console.WriteLine($"[Mqtt] {selectError}");
                    return;
                }
                if (name != "off") _lastEffect = name;
                Console.WriteLine($"[Mqtt] select command → {selected}");
                await Task.Delay(1500);
                await PublishStateAsync();
            }
            else if (topic == NotifySetTopic)
            {
                // A text entity sends the bare string; give it the configured banner lifetime.
                string body = payload.TrimStart().StartsWith('{')
                    ? payload
                    : JsonSerializer.Serialize(new { text = payload, duration = _notifyDurationSec });

                if (!_web.TryPostNotification(body, out string? notifyError))
                    Console.WriteLine($"[Mqtt] rejected notification: {notifyError}");
                else
                    Console.WriteLine($"[Mqtt] notification queued: {payload}");

                await Task.Delay(1500);
                await PublishStateAsync();
            }
            else if (topic == NotifyDurationSetTopic)
            {
                if (!TryParseDurationSeconds(payload, out int seconds, out string? durationError))
                {
                    Console.WriteLine($"[Mqtt] rejected banner duration on {topic}: {durationError}");
                }
                else
                {
                    _notifyDurationSec = seconds;
                    Console.WriteLine($"[Mqtt] banner duration set to {seconds}s.");
                }
                await PublishAsync(NotifyDurationStateTopic, _notifyDurationSec.ToString(System.Globalization.CultureInfo.InvariantCulture), retain: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mqtt] command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse a Home Assistant number-entity command into a banner lifetime, clamped to the
    /// range the number entity advertises.
    /// </summary>
    public static bool TryParseDurationSeconds(string payload, out int seconds, out string? error)
    {
        seconds = DefaultNotifyDurationSec;
        error = null;

        string body = (payload ?? "").Trim();
        if (body.Length == 0)
        {
            error = "empty payload.";
            return false;
        }

        // HA sends a plain number, but an object payload is tolerated for direct publishes.
        if (body.StartsWith('{'))
        {
            string raw;
            try
            {
                using var doc = JsonDocument.Parse(body);
                doc.RootElement.TryGetProperty("duration", out var d);
                raw = d.ToString();
            }
            catch (JsonException)
            {
                error = "payload is not valid JSON.";
                return false;
            }

            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int fromJson))
            {
                error = "JSON must contain a \"duration\" number.";
                return false;
            }
            seconds = ClampSeconds(fromJson);
            return true;
        }

        if (!int.TryParse(body, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"\"{body}\" is not a number of seconds.";
            return false;
        }
        seconds = ClampSeconds(parsed);
        return true;
    }

    /// <summary>Keep the value inside the range the number entity advertises.</summary>
    private static int ClampSeconds(int seconds)
    {
        if (seconds < MinNotifyDurationSec || seconds > MaxNotifyDurationSec)
            Console.WriteLine($"[Mqtt] banner duration {seconds}s is outside {MinNotifyDurationSec}–{MaxNotifyDurationSec}s; clamped.");
        return Math.Clamp(seconds, MinNotifyDurationSec, MaxNotifyDurationSec);
    }

    /// <summary>
    /// Parse a Home Assistant light command payload: bare <c>ON</c>/<c>OFF</c>/<c>TOGGLE</c>, or
    /// JSON <c>{"state":"ON"}</c> / <c>{"effect":"neon_pulse_grid"}</c> / both.
    /// </summary>
    public static bool TryParseLightCommand(string payload, out string? state, out string? effect, out string? error)
    {
        state = null;
        effect = null;
        error = null;

        string body = payload.Trim();
        if (body.Length == 0)
        {
            error = "empty payload.";
            return false;
        }

        if (body.StartsWith('{'))
        {
            Dictionary<string, JsonElement>? json;
            try { json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
            catch { json = null; }

            if (json == null)
            {
                error = "payload is not valid JSON.";
                return false;
            }
            if (json.TryGetValue("state", out var s) && s.ValueKind == JsonValueKind.String)
                state = s.GetString()?.ToUpperInvariant();
            if (json.TryGetValue("effect", out var ef) && ef.ValueKind == JsonValueKind.String)
                effect = ef.GetString();

            if (state == null && effect == null)
            {
                error = "JSON must contain a \"state\" and/or \"effect\" string.";
                return false;
            }
        }
        else
        {
            state = body.ToUpperInvariant();
        }

        if (state is not (null or "ON" or "OFF" or "TOGGLE"))
        {
            error = $"unknown state '{state}' (expected ON or OFF).";
            return false;
        }

        return true;
    }

    // ─── State ───

    /// <summary>Heartbeat entry point. Must never throw: an unhandled exception on a timer
    /// callback would take the whole process down.</summary>
    private void PublishState()
    {
        try { PublishStateAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine($"[Mqtt] state publish skipped: {ex.Message}"); }
    }

    /// <summary>
    /// Map the app's selected-shader label onto one of the select entity's options:
    /// "off", "built-in default", or a bare shader name (the status snapshot reports file names).
    /// </summary>
    private static string ShaderOption(string? selected)
        => string.IsNullOrEmpty(selected) || selected.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? "off"
            : Path.GetFileNameWithoutExtension(selected);

    private async Task PublishStateAsync()
    {
        if (!_client.IsConnected) return;

        var snap = _web.StatusSnapshot;
        string? selected = snap?.SelectedShader;
        bool on = selected != null && !selected.Equals("off", StringComparison.OrdinalIgnoreCase);
        string effect = on ? Path.GetFileNameWithoutExtension(selected!) : "";

        await PublishAsync(StateTopic, JsonSerializer.Serialize(new { state = on ? "ON" : "OFF", effect }), retain: true);
        await PublishAsync(ShaderSelectStateTopic, ShaderOption(selected), retain: true);

        // Banner entities: what is on the matrix right now, and the configured lifetime.
        await PublishAsync(NotifyStateTopic, snap?.ActiveNotification ?? "", retain: true);
        await PublishAsync(NotifyDurationStateTopic,
            _notifyDurationSec.ToString(System.Globalization.CultureInfo.InvariantCulture), retain: true);

        if (snap == null) return;
        await PublishAsync(AttributesTopic, JsonSerializer.Serialize(new
        {
            shader = selected,
            audio_enabled = snap.AudioEnabled,
            audio_source = snap.AudioSource,
            frames_sent = snap.FramesSent,
            send_errors = snap.SendErrors,
            string_enabled = snap.StringEnabled,
            string_size = snap.StringSize,
            string_universe = snap.StringUniverse,
            string_frames_sent = snap.StringFramesSent,
            string_send_errors = snap.StringSendErrors,
            uptime_secs = (int)snap.UptimeSecs,
        }), retain: true);
    }

    private async Task PublishAsync(string topic, string payload, bool retain)
    {
        try
        {
            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mqtt] publish to {topic} failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _heartbeat?.Dispose();
        try
        {
            if (_client.IsConnected)
            {
                // Publish offline explicitly so the entity goes unavailable immediately instead of
                // waiting for the broker to notice the dropped connection.
                PublishAsync(AvailabilityTopic, "offline", retain: true).GetAwaiter().GetResult();
                _client.DisconnectAsync().GetAwaiter().GetResult();
            }
        }
        catch { /* shutting down */ }
        _client.Dispose();
    }
}
