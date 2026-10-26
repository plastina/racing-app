using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace RacingApp;

public partial class MainWindow : Window
{
    // RCGTII's per-effect default ("3_Speed" setting).
    private const byte DefaultSpeed = 50;

    private readonly ObservableCollection<SensorRow> _temperatures = new();
    private readonly ObservableCollection<SensorRow> _fans = new();
    private readonly ObservableCollection<SensorRow> _voltages = new();
    private readonly ObservableCollection<FanChannelView> _fanViews = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private HardwareService? _hardware;
    private LedController? _leds;
    private LedState? _original;
    private RamLedController? _ram;
    private RamLedState? _ramOriginal;
    private SoftwareEffectEngine? _engine;
    private volatile float _cpuTemperature = float.NaN;
    private string? _cpuTemperatureKey;
    private IReadOnlyList<SensorReading> _lastReadings = Array.Empty<SensorReading>();
    private FanController? _fanController;
    private string _fanSummary = "";
    private bool _fanFailsafeShown;
    private bool _refreshing;
    private bool _loadingStartupState;
    private readonly TrayIcon _tray;
    private bool _exitRequested;
    private bool _trayHintShown;
    private UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        if (Updater.IsReleaseBuild)
            Title = $"Racing Studio {Updater.CurrentVersion}";
        _tray = new TrayIcon(ShowFromTray, ExitFromTray);
        TemperatureList.ItemsSource = _temperatures;
        FanList.ItemsSource = _fans;
        VoltageList.ItemsSource = _voltages;
        FanControlList.ItemsSource = _fanViews;

        // Chip effects first, then the app-computed ones.
        BoardEditor.Effects = LedEffect.All.Cast<object>().Concat(SoftEffectOption.All).ToList();
        BoardEditor.Speed = DefaultSpeed;
        RamEditor.Effects = RamLedEffect.All.Cast<object>().Concat(SoftEffectOption.All).ToList();
        UpdateSyncUi();
    }

    /// <summary>Started by the logon task: stay in the notification area and resume the last configuration.</summary>
    public bool StartInTray { get; init; }

    private bool IsSynced => SyncCheck.IsChecked == true && _leds is not null;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (_pendingUpdate is not null)
            Dispatcher.BeginInvoke(OfferPendingUpdate);
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Windows is logging off or shutting down: really exit (and restore the lighting) instead of hiding.</summary>
    public void PrepareForSessionEnd() => _exitRequested = true;

    /// <summary>The window's close button only hides it; "Sair" in the tray menu really exits.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _tray.ShowMessage("Continua rodando na bandeja. Para fechar de vez: botão direito no ícone → Sair.");
            _trayHintShown = true;
        }
    }

    private float? CpuTemperature() => float.IsNaN(_cpuTemperature) ? null : _cpuTemperature;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (StartInTray)
        {
            Hide();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
        }

        // Started now, offered once the hardware is set up so an update never closes the app halfway through it.
        Task<UpdateInfo?> updateCheck = Updater.CheckAsync();

        string iniPath = Path.Combine(AppContext.BaseDirectory, "BoardConfig.ini");
        try
        {
            _hardware = await Task.Run(() => new HardwareService(iniPath));
        }
        catch (Exception ex)
        {
            BoardText.Text = $"Não foi possível abrir o acesso ao hardware: {ex.Message}";
            LedStatus.Text = "Placa: LEDs indisponíveis.";
            RamStatus.Text = "Memórias: indisponíveis.";
            FanStatus.Text = "Controle das fans indisponível.";
            await ReportUpdateAsync(updateCheck);
            return;
        }

        BoardText.Text = $"{_hardware.BoardManufacturer} {_hardware.BoardName}" +
                         (_hardware.Profile is null ? " · sem perfil no Config.ini" : " · perfil BIOSTAR carregado") +
                         (_hardware.HasHardwareAccess ? "" : "  ⚠ sem administrador ou PawnIO: leituras limitadas");

        _timer.Tick += async (_, _) => await RefreshSensorsAsync();
        _timer.Start();
        await RefreshSensorsAsync();
        await InitFansAsync();
        await InitBoardLedsAsync();
        await InitRamAsync();
        UpdateSyncUi();
        await InitStartupOptionAsync();

        if (_leds is not null || _ram is not null)
        {
            _engine = new SoftwareEffectEngine(_leds, _ram);
            _engine.Status += text => Dispatcher.BeginInvoke(() => AppEffectStatus.Text = text);
        }

        // Show the last configuration the user applied (including app effects) instead of raw chip state.
        LightingSettings? saved = LightingSettings.Load();
        if (saved?.BoardUi is not null || saved?.RamUi is not null)
        {
            if (saved.BoardUi is not null)
                BoardEditor.SetState(saved.BoardUi);
            if (saved.RamUi is not null)
                RamEditor.SetState(saved.RamUi);
            SyncCheck.IsChecked = saved.Sync;

            // After a reboot the chips are back on the BIOS configuration: re-apply it.
            if (StartInTray && (_leds is not null || _ram is not null))
                await ApplyCurrentAsync();
        }

        await ReportUpdateAsync(updateCheck);
    }

    /// <summary>
    /// Offers a newer release: right away if the window is open, otherwise through a tray notification (when started
    /// with Windows) and again the next time the window is opened.
    /// </summary>
    private async Task ReportUpdateAsync(Task<UpdateInfo?> check)
    {
        try
        {
            _pendingUpdate = await check;
        }
        catch (Exception)
        {
            // Offline, GitHub rate limit, etc.: just try again on the next start.
            return;
        }

        if (_pendingUpdate is null)
            return;

        if (IsVisible)
            OfferPendingUpdate();
        else
            _tray.ShowMessage($"Versão {_pendingUpdate.Version} disponível. Clique para atualizar.", ShowFromTray);
    }

    private async void OfferPendingUpdate()
    {
        if (_pendingUpdate is not UpdateInfo update)
            return;

        _pendingUpdate = null;
        MessageBoxResult answer = MessageBox.Show(this,
            $"A versão {update.Version} do Racing Studio está disponível (esta é a {Updater.CurrentVersion}).\n\n" +
            "Atualizar agora? O app fecha e abre de novo sozinho.",
            "Atualização disponível", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
            return;

        string title = Title;
        Title = $"{title} · baixando a versão {update.Version}…";
        IsEnabled = false;
        try
        {
            await Updater.InstallAsync(update);
            Updater.StartUpdated();
        }
        catch (Exception ex)
        {
            Title = title;
            IsEnabled = true;
            MessageBox.Show(this, $"Não foi possível atualizar: {ex.Message}", "Atualização",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The new process waits for this one to restore the fans and lighting and exit.
        _exitRequested = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_engine is { IsRunning: true })
        {
            // Leave the lighting in the last applied configuration instead of a random animation frame.
            // RestoreBaselineAsync only touches the controllers, so it can run off the (blocked) UI thread.
            Task.Run(async () =>
            {
                await _engine.StopAsync();
                await RestoreBaselineAsync();
            }).Wait(TimeSpan.FromSeconds(5));
        }

        // Closing the Computer also restores them, but say it explicitly: fans must never stay on a manual duty.
        _fanController?.RestoreBios();
        _hardware?.Dispose();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private async Task RefreshSensorsAsync()
    {
        if (_hardware is null || _refreshing)
            return;

        _refreshing = true;
        try
        {
            IReadOnlyList<SensorReading> readings = await Task.Run(_hardware.ReadSensors);
            SensorReading? cpu = readings.FirstOrDefault(r => r.Type == SensorType.Temperature && r.Label.Contains("Tctl"))
                                 ?? readings.FirstOrDefault(r => r.Type == SensorType.Temperature && r.Label.StartsWith("CPU"));
            _cpuTemperature = cpu?.Value ?? float.NaN;
            _cpuTemperatureKey = cpu?.Key;
            _lastReadings = readings;
            Update(_temperatures, readings.Where(r => r.Type == SensorType.Temperature), "0", "°C");
            Update(_fans, readings.Where(r => r.Type == SensorType.Fan), "0", "RPM");
            Update(_voltages, readings.Where(r => r.Type == SensorType.Voltage), "0.000", "V");

            if (_fanController is FanController fans)
            {
                float? cpuTemperature = cpu?.Value;
                try
                {
                    await Task.Run(() => fans.Tick(readings, cpuTemperature));
                }
                catch (Exception ex)
                {
                    FanStatus.Text = $"Erro ao escrever nas fans: {ex.Message}";
                }

                UpdateFanRows(readings, fans.FailsafeActive);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void Update(ObservableCollection<SensorRow> rows, IEnumerable<SensorReading> readings, string format, string unit)
    {
        foreach (SensorReading reading in readings)
        {
            SensorRow? row = rows.FirstOrDefault(r => r.Key == reading.Key);
            if (row is null)
            {
                row = new SensorRow(reading.Key, reading.Label);
                rows.Add(row);
            }

            row.ValueText = reading.Value is null ? "—" : $"{reading.Value.Value.ToString(format, CultureInfo.CurrentCulture)} {unit}";
        }
    }

    private async Task InitBoardLedsAsync()
    {
        if (_hardware is null || !_hardware.HasHardwareAccess)
        {
            LedStatus.Text = "Placa: abra o app como administrador com o PawnIO instalado.";
            return;
        }

        var bus = await Task.Run(_hardware.FindAuxBus);
        if (bus is null)
        {
            LedStatus.Text = "Placa: não encontrei o SMBus auxiliar (0x0B20) pelo PawnIO.";
            return;
        }

        var leds = new LedController(bus, _hardware.Gate);
        LedState? state = await Task.Run(leds.ReadState);
        if (state is null)
        {
            LedStatus.Text = "Placa: o controlador 0x30 não respondeu.";
            return;
        }

        _leds = leds;
        _original = state;
        ShowBoardState(state);
        BoardEditor.IsEnabled = true;
        LedStatus.Text = $"Placa: {state.Color} · {BoardEffectName(state.Mode)} · velocidade 0x{state.Speed:X2}" +
                         (state.Enabled ? "" : " · conector 5V DESLIGADO (a cor só aparece com ele ligado)");
    }

    private async Task InitRamAsync()
    {
        if (_hardware is null || !_hardware.HasHardwareAccess)
        {
            RamStatus.Text = "Memórias: indisponíveis sem administrador e PawnIO.";
            return;
        }

        var bus = await Task.Run(_hardware.FindMainBus);
        if (bus is null)
        {
            RamStatus.Text = "Memórias: não encontrei o SMBus principal (0x0B00) pelo PawnIO.";
            return;
        }

        var ram = new RamLedController(bus, _hardware.Gate);
        RamLedState? state = await Task.Run(ram.ReadState);
        if (state is null)
        {
            RamStatus.Text = "Memórias: nenhum controlador RGB respondeu em 0x77.";
            return;
        }

        if (state.ChipName != RamLedController.SupportedChip)
        {
            RamStatus.Text = $"Memórias: chip \"{state.ChipName}\" não testado; controle desativado por segurança.";
            return;
        }

        _ram = ram;
        _ramOriginal = state;
        ShowRamState(state);
        RamStatus.Text = $"Memórias: {state.ChipName} · {state.FirstColor} · {RamLedEffect.NameOf(state.Mode)}";
    }

    private async Task InitFansAsync()
    {
        if (_hardware is null || !_hardware.HasHardwareAccess)
        {
            FanStatus.Text = "Fans: abra o app como administrador com o PawnIO instalado.";
            return;
        }

        IReadOnlyList<FanChannel> channels = await Task.Run(_hardware.FindFanControls);
        if (channels.Count == 0)
        {
            FanStatus.Text = "O LibreHardwareMonitor não oferece controle PWM para o Super I/O desta placa.";
            return;
        }

        var sources = _lastReadings.Where(r => r.Type == SensorType.Temperature)
                                   .Select(r => new TemperatureSource(r.Key, r.Label))
                                   .ToList();
        foreach (FanChannel channel in channels)
            _fanViews.Add(new FanChannelView(channel, sources, _cpuTemperatureKey));

        _fanController = new FanController(_hardware, channels);

        // The BIOS owns the fans again after every restart of the app: resume what was last applied.
        List<FanSettings> saved = LightingSettings.Load()?.Fans ?? new List<FanSettings>();
        foreach (FanSettings settings in saved)
            _fanViews.FirstOrDefault(v => v.Channel.Index == settings.Index)?.SetSettings(settings);
        _fanController.Configure(saved.Where(s => channels.Any(c => c.Index == s.Index)));

        _fanSummary = DescribeFans(saved);
        FanStatus.Text = _fanSummary;
        FanApplyButton.IsEnabled = true;
        FanBiosButton.IsEnabled = true;
    }

    private void OnFanApply(object sender, RoutedEventArgs e)
    {
        if (_fanController is null)
            return;

        var settings = new List<FanSettings>();
        foreach (FanChannelView view in _fanViews)
        {
            if (view.ToSettings(out string? error) is not FanSettings s)
            {
                FanStatus.Text = error;
                return;
            }

            settings.Add(s);
        }

        // Written by the next sensor tick (within a second).
        _fanController.Configure(settings);
        SaveFans(settings);
    }

    private async void OnFanBios(object sender, RoutedEventArgs e)
    {
        if (_fanController is null)
            return;

        foreach (FanChannelView view in _fanViews)
            view.ModeIndex = (int)FanMode.Bios;
        await Task.Run(_fanController.RestoreBios);

        // Keep each row's fixed value and curve so switching back is quick.
        SaveFans(_fanViews.Select(v => v.ToSettings(out _)).OfType<FanSettings>().ToList());
    }

    private void SaveFans(List<FanSettings> fans)
    {
        _fanSummary = DescribeFans(fans);
        FanStatus.Text = _fanSummary;
        LightingSettings settings = LightingSettings.Load() ?? new LightingSettings();
        settings.Fans = fans;
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FanStatus.Text = $"Aplicado, mas não foi possível salvar: {ex.Message}";
        }
    }

    private static string DescribeFans(IReadOnlyCollection<FanSettings> fans) =>
        fans.All(f => f.Mode == FanMode.Bios)
            ? "Todas as fans no automático do BIOS."
            : "Fans controladas pelo app enquanto ele estiver aberto (inclusive na bandeja); ao sair, voltam ao BIOS. " +
              $"Com a CPU acima de {FanController.FailsafeTemperature:0} °C, todas vão a 100 %.";

    private void UpdateFanRows(IReadOnlyList<SensorReading> readings, bool failsafe)
    {
        foreach (FanChannelView view in _fanViews)
        {
            float? rpm = readings.FirstOrDefault(r => r.Key == view.Channel.FanKey)?.Value;
            float? duty = readings.FirstOrDefault(r => r.Key == view.Channel.ControlKey)?.Value;
            view.StatusText = $"{(rpm is float r ? $"{r:0} RPM" : "—")} · {(duty is float d ? $"{d:0} %" : "—")}";
        }

        if (failsafe != _fanFailsafeShown)
        {
            _fanFailsafeShown = failsafe;
            FanStatus.Text = failsafe
                ? $"⚠ CPU acima de {FanController.FailsafeTemperature:0} °C: fans controladas pelo app em 100 %."
                : _fanSummary;
        }
    }

    private async Task InitStartupOptionAsync()
    {
        if (_hardware is null || !_hardware.HasHardwareAccess)
            return;

        _loadingStartupState = true;
        bool exists = await Task.Run(StartupTask.Exists);
        StartupCheck.IsChecked = exists;
        _loadingStartupState = false;
        StartupCheck.IsEnabled = true;

        // Older versions registered "--apply-saved"; keep the user's choice but point it at the tray mode.
        if (exists && !StartInTray && await Task.Run(StartupTask.Create) is null)
            StatusMessage("Inicialização com o Windows atualizada: o app abre na bandeja e retoma sua configuração.");
    }

    private void OnSyncChanged(object sender, RoutedEventArgs e) => UpdateSyncUi();

    /// <summary>Keeps the RAM tab, its note and the buttons consistent with the sync choice and what was found.</summary>
    private void UpdateSyncUi()
    {
        if (RamEditor is null)
            return;

        RamSyncNote.Visibility = SyncCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RamEditor.IsEnabled = _ram is not null && !IsSynced;
        bool anyDevice = _leds is not null || _ram is not null;
        ApplyButton.IsEnabled = anyDevice;
        RestoreButton.IsEnabled = anyDevice;
    }

    /// <summary>
    /// Applies what each tab shows. Chip effects are written once (and saved for the logon task); app effects are
    /// handed to the engine, which keeps animating until another Apply or Restore.
    /// </summary>
    private async void OnApply(object sender, RoutedEventArgs e) => await ApplyCurrentAsync();

    private async Task ApplyCurrentAsync()
    {
        ISoftwareEffect? boardApp = BoardEditor.BuildAppEffect(CpuTemperature, out string? boardError);
        ISoftwareEffect? ramApp = null;
        string? ramError = null;
        if (_ram is not null)
            ramApp = IsSynced ? boardApp : RamEditor.BuildAppEffect(CpuTemperature, out ramError);

        if ((_leds is not null && boardError is not null) || ramError is not null)
        {
            AppEffectStatus.Text = boardError ?? ramError;
            AppEffectStatus.Visibility = Visibility.Visible;
            return;
        }

        SetBusy(true);
        try
        {
            await StopAppEffectAsync();

            bool boardOk = false, ramOk = false;
            Rgb boardColor = BoardEditor.Color, ramColor = default;
            byte boardMode = 0, boardSpeed = BoardEditor.Speed, ramMode = 1;

            if (_leds is not null && boardApp is null && BoardEditor.SelectedEffect is LedEffect boardEffect)
            {
                boardMode = boardEffect.RegisterValue;
                boardOk = await Task.Run(() => _leds.Apply(boardColor, boardMode, boardSpeed));
                LedStatus.Text = boardOk
                    ? $"Placa: {boardColor} · {boardEffect.Name} · velocidade 0x{boardSpeed:X2}"
                    : $"Placa: o controlador não confirmou {boardColor} · {boardEffect.Name}. Tente de novo.";
            }

            if (_ram is not null && ramApp is null)
            {
                (ramColor, ramMode) = IsSynced && BoardEditor.SelectedEffect is LedEffect followed
                    ? (BoardEditor.Color, followed.RamMode)
                    : (RamEditor.Color, (RamEditor.SelectedEffect as RamLedEffect)?.Mode ?? (byte)1);
                ramOk = await Task.Run(() => _ram.Apply(ramColor, ramMode));
                string source = IsSynced ? " (seguindo a placa)" : "";
                RamStatus.Text = ramOk
                    ? $"Memórias: {ramColor} · {RamLedEffect.NameOf(ramMode)}{source}"
                    : $"Memórias: o controlador não confirmou {ramColor} · {RamLedEffect.NameOf(ramMode)}. Tente de novo.";
            }

            SaveApplied(boardOk, boardColor, boardMode, boardSpeed, ramOk, ramColor, ramMode);

            ISoftwareEffect? boardRun = _leds is not null ? boardApp : null;
            if (boardRun is not null || ramApp is not null)
            {
                _engine?.Start(boardRun, ramApp, boardSpeed);
                if (boardRun is not null)
                    LedStatus.Text = $"Placa: {boardRun.Name} (efeito do app)";
                if (ramApp is not null)
                    RamStatus.Text = $"Memórias: {ramApp.Name} (efeito do app{(IsSynced ? ", seguindo a placa" : "")})";
                AppEffectStatus.Text = "Efeito do app iniciando...";
                AppEffectStatus.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await StopAppEffectAsync();

            bool boardOk = false, ramOk = false;
            if (_leds is not null && _original is not null)
            {
                LedState original = _original;
                ShowBoardState(original);
                boardOk = await Task.Run(() => _leds.Apply(original.Color, original.Mode, original.Speed));
                LedStatus.Text = boardOk
                    ? $"Placa: original restaurado ({original.Color} · {BoardEffectName(original.Mode)})"
                    : "Placa: falha ao restaurar. Tente de novo.";
            }

            if (_ram is not null && _ramOriginal is not null)
            {
                RamLedState original = _ramOriginal;
                ShowRamState(original);
                ramOk = await Task.Run(() => _ram.ApplyRaw(original.EffectColors, original.Mode));
                RamStatus.Text = ramOk
                    ? $"Memórias: original restaurado ({original.FirstColor} · {RamLedEffect.NameOf(original.Mode)})"
                    : "Memórias: falha ao restaurar. Tente de novo.";
            }

            SaveApplied(boardOk, _original?.Color ?? default, _original?.Mode ?? 0, _original?.Speed ?? 0,
                        ramOk, _ramOriginal?.FirstColor ?? default, _ramOriginal?.Mode ?? 1);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Stops a running app effect; the caller then writes the next configuration.</summary>
    private async Task StopAppEffectAsync()
    {
        if (_engine is not null)
            await _engine.StopAsync();
        AppEffectStatus.Visibility = Visibility.Collapsed;
    }

    /// <summary>Last saved configuration (or the original one), used when the app closes during an app effect.</summary>
    private async Task RestoreBaselineAsync()
    {
        LightingSettings? saved = LightingSettings.Load();
        if (_leds is not null)
        {
            (Rgb color, byte mode, byte speed) = saved is { HasBoard: true }
                ? (LightingSettings.ParseColor(saved.BoardColor), saved.BoardMode, saved.BoardSpeed)
                : (_original!.Color, _original.Mode, _original.Speed);
            await Task.Run(() => _leds.Apply(color, mode, speed));
        }

        if (_ram is not null)
        {
            if (saved is { HasRam: true })
            {
                Rgb color = LightingSettings.ParseColor(saved.RamColor);
                await Task.Run(() => _ram.Apply(color, saved.RamMode));
            }
            else if (_ramOriginal is not null)
            {
                RamLedState original = _ramOriginal;
                await Task.Run(() => _ram.ApplyRaw(original.EffectColors, original.Mode));
            }
        }
    }

    private async void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStartupState)
            return;

        bool enable = StartupCheck.IsChecked == true;
        StartupCheck.IsEnabled = false;
        string? error = await Task.Run(() => enable ? StartupTask.Create() : StartupTask.Delete());
        StartupCheck.IsEnabled = true;
        if (error is null)
        {
            StatusMessage(enable
                ? "Ao entrar no Windows, o Racing Studio abre na bandeja e retoma a última configuração aplicada (inclusive efeitos do app)."
                : "O Racing Studio não abrirá mais com o Windows.");
        }
        else
        {
            StatusMessage($"Não foi possível {(enable ? "criar" : "remover")} a tarefa de inicialização: {error}");
            SetStartupCheck(!enable);
        }
    }

    private void SetStartupCheck(bool value)
    {
        _loadingStartupState = true;
        StartupCheck.IsChecked = value;
        _loadingStartupState = false;
    }

    private void StatusMessage(string text) => StartupStatus.Text = text;

    /// <summary>
    /// Remembers what each tab shows (to resume it, app effects included, when the app starts with Windows) and
    /// the chip values that were written successfully (for <c>--apply-saved</c>).
    /// </summary>
    private void SaveApplied(bool boardOk, Rgb boardColor, byte boardMode, byte boardSpeed,
                             bool ramOk, Rgb ramColor, byte ramMode)
    {
        LightingSettings settings = LightingSettings.Load() ?? new LightingSettings();
        settings.Sync = SyncCheck.IsChecked == true;
        if (_leds is not null)
            settings.BoardUi = BoardEditor.GetState();
        if (_ram is not null)
            settings.RamUi = RamEditor.GetState();
        if (boardOk)
        {
            settings.HasBoard = true;
            settings.BoardColor = boardColor.ToString();
            settings.BoardMode = boardMode;
            settings.BoardSpeed = boardSpeed;
        }

        if (ramOk)
        {
            settings.HasRam = true;
            settings.RamColor = ramColor.ToString();
            settings.RamMode = ramMode;
        }

        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage($"Não foi possível salvar a configuração: {ex.Message}");
        }
    }

    private void SetBusy(bool busy)
    {
        ApplyButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        BoardEditor.IsEnabled = !busy && _leds is not null;
        RamEditor.IsEnabled = !busy && _ram is not null && !IsSynced;
    }

    private void ShowBoardState(LedState state)
    {
        BoardEditor.Color = state.Color;
        BoardEditor.Speed = state.Speed;
        if (LedEffect.All.FirstOrDefault(e => e.RegisterValue == state.Mode) is LedEffect effect)
            BoardEditor.SelectedEffect = effect;
    }

    private void ShowRamState(RamLedState state)
    {
        RamEditor.Color = state.FirstColor;
        if (RamLedEffect.All.FirstOrDefault(e => e.Mode == state.Mode) is RamLedEffect effect)
            RamEditor.SelectedEffect = effect;
    }

    private static string BoardEffectName(byte mode) =>
        LedEffect.All.FirstOrDefault(e => e.RegisterValue == mode)?.Name ?? $"efeito 0x{mode:X2}";
}

public sealed class SensorRow : INotifyPropertyChanged
{
    private string _valueText = "—";

    public SensorRow(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Label { get; }

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value)
                return;
            _valueText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
        }
    }
}
