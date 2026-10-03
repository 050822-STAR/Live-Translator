using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

using LiveTranslator.App.Services;
using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;
using LiveTranslator.Core.Settings;

namespace LiveTranslator.App.Views;

/// <summary>Edits a cloned draft of the settings; nothing touches the running app until "保存并应用".</summary>
public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly AppHost _host;
    private ProviderProfile? _selectedProfile;
    private string _testResult = "";
    private string _roleText = "";
    private string _presetNotes = "";
    private bool _syncingKey;
    private CancellationTokenSource? _testCts;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        Draft = SettingsStore.Clone(host.Settings);
        Profiles = new ObservableCollection<ProviderProfile>(Draft.Profiles);
        InitializeComponent();
        SelectedProfile = Draft.FindProfile(Draft.ActiveProfileId) ?? Profiles.FirstOrDefault();
        UpdateRoleText();
        Closed += (_, _) => _testCts?.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Draft { get; }
    public ObservableCollection<ProviderProfile> Profiles { get; }
    public ObservableCollection<string> FetchedModels { get; } = [];
    public IReadOnlyList<ProviderPreset> Presets => ProviderPresets.All;
    public IReadOnlyList<LanguageInfo> Languages => Core.Models.Languages.All;
    public Array Protocols { get; } = Enum.GetValues<ProviderProtocol>();

    public ProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
                return;
            _selectedProfile = value;
            OnPropertyChanged();
            FetchedModels.Clear();
            TestResult = "";
            PresetNotes = value is null ? "" : ProviderPresets.Find(value.PresetId)?.Notes ?? "";
            if (ProfileForm is null)
                return; // still inside InitializeComponent
            ProfileForm.IsEnabled = value is not null;
            _syncingKey = true;
            ApiKeyBox.Password = value?.ApiKey ?? "";
            _syncingKey = false;
        }
    }

    public string TestResult { get => _testResult; private set { _testResult = value; OnPropertyChanged(); } }
    public string RoleText { get => _roleText; private set { _roleText = value; OnPropertyChanged(); } }
    public string PresetNotes { get => _presetNotes; private set { _presetNotes = value; OnPropertyChanged(); } }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void UpdateRoleText()
    {
        var active = Profiles.FirstOrDefault(p => p.Id == Draft.ActiveProfileId);
        var backup = Profiles.FirstOrDefault(p => p.Id == Draft.BackupProfileId);
        RoleText = $"主服务：{active?.Name ?? "（未设置）"}\n备用（竞速）：{backup?.Name ?? "无"}";
    }

    // ───────────────────────────── profiles ─────────────────────────────

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ProviderPreset preset)
            return;
        var profile = ProviderPresets.CreateProfile(preset.Id);
        profile.Name = UniqueName(preset.DisplayName);
        Profiles.Add(profile);
        SelectedProfile = profile;
        ProfileList.ScrollIntoView(profile);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
            return;
        var copy = SelectedProfile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(SelectedProfile.Name + " 副本");
        Profiles.Insert(Profiles.IndexOf(SelectedProfile) + 1, copy);
        SelectedProfile = copy;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
            return;
        if (Profiles.Count == 1)
        {
            MessageBox.Show(this, "至少需要保留一个翻译服务。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除“{profile.Name}”？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        if (Draft.ActiveProfileId == profile.Id)
            Draft.ActiveProfileId = Profiles[0].Id;
        if (Draft.BackupProfileId == profile.Id || Draft.BackupProfileId == Draft.ActiveProfileId)
            Draft.BackupProfileId = "";
        SelectedProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
        UpdateRoleText();
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (int i = 2; Profiles.Any(p => p.Name == candidate); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
            return;
        Draft.ActiveProfileId = SelectedProfile.Id;
        if (Draft.BackupProfileId == SelectedProfile.Id)
            Draft.BackupProfileId = "";
        UpdateRoleText();
    }

    private void SetBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
            return;
        if (SelectedProfile.Id == Draft.ActiveProfileId)
        {
            TestResult = "备用服务不能与主服务相同。";
            return;
        }
        Draft.BackupProfileId = SelectedProfile.Id;
        UpdateRoleText();
    }

    private void ClearBackup_Click(object sender, RoutedEventArgs e)
    {
        Draft.BackupProfileId = "";
        UpdateRoleText();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKey && SelectedProfile is not null)
            SelectedProfile.ApiKey = ApiKeyBox.Password;
    }

    private void ShowKey_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowKeyBox.IsChecked == true;
        if (!show)
        {
            _syncingKey = true;
            ApiKeyBox.Password = SelectedProfile?.ApiKey ?? "";
            _syncingKey = false;
        }
        ApiKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─────────────────────────── test & models ───────────────────────────

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
            return;
        TestResult = "正在获取模型列表…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await ProviderFactory.Create(profile, _host.Http).ListModelsAsync(cts.Token);
            FetchedModels.Clear();
            foreach (var model in models)
                FetchedModels.Add(model);
            TestResult = models.Count == 0 ? "该服务不提供模型列表，请手动填写模型名称。" : $"获取到 {models.Count} 个模型，可在下拉框中选择。";
            if (models.Count > 0)
                ModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex) when (ex is ProviderException or FormatException or HttpRequestException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            TestResult = "获取失败：" + (ex is OperationCanceledException ? "请求超时" : ex.Message);
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
            return;
        if (ProviderFactory.Validate(profile) is { } problem)
        {
            TestResult = problem;
            return;
        }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        var language = Core.Models.Languages.Get(Draft.TargetLanguage);
        var request = new TranslationRequest(TestInput.Text, language, [], PromptBuilder.RenderSystemPrompt(Draft.SystemPrompt, language));
        var provider = ProviderFactory.Create(profile, _host.Http);

        TestButton.IsEnabled = false;
        TestResult = "请求中…";
        var sw = Stopwatch.StartNew();
        long? firstToken = null;
        var output = new StringBuilder();
        var filter = new ThinkTagFilter();
        try
        {
            await foreach (var chunk in provider.TranslateStreamAsync(request, ct))
            {
                var visible = filter.Push(chunk);
                if (visible.Length == 0)
                    continue;
                firstToken ??= sw.ElapsedMilliseconds;
                output.Append(visible);
                TestResult = output.ToString().TrimStart();
            }
            output.Append(filter.Flush());
            TestResult = $"{output.ToString().Trim()}\n\n首字 {firstToken ?? sw.ElapsedMilliseconds} ms · 完成 {sw.ElapsedMilliseconds} ms" +
                         "（首次请求包含建立连接的时间，再测一次可看到连接复用后的延迟）";
        }
        catch (ProviderException ex)
        {
            TestResult = "失败：" + ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Window closed or test restarted.
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ─────────────────────────────── misc ───────────────────────────────

    private void ResetPrompt_Click(object sender, RoutedEventArgs e) => PromptBox.Text = PromptBuilder.DefaultSystemPrompt;

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_host.Log.Directory);

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(AppHost.DataDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Draft.Profiles = [.. Profiles];
        foreach (var id in new[] { Draft.ActiveProfileId, Draft.BackupProfileId })
        {
            if (Draft.FindProfile(id) is not { } profile || ProviderFactory.Validate(profile) is not { } problem)
                continue;
            Tabs.SelectedIndex = 0;
            SelectedProfile = profile;
            ValidationText.Text = $"“{profile.Name}”：{problem}";
            return;
        }
        if (Draft.Network.Proxy is { Length: > 0 } proxy &&
            !proxy.Equals("none", StringComparison.OrdinalIgnoreCase) && !proxy.Equals("direct", StringComparison.OrdinalIgnoreCase) &&
            !Uri.TryCreate(proxy, UriKind.Absolute, out _))
        {
            Tabs.SelectedIndex = 3;
            ValidationText.Text = "代理地址格式不正确，例如 http://127.0.0.1:7890";
            return;
        }

        _host.ApplySettings(Draft);
        DialogResult = true;
    }
}
