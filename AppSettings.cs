using System;
using System.IO;
using System.Text.Json;

namespace TaskbarInfo
{
    public class AppSettings
    {
        public double Width { get; set; } = 400;
        public string TranslationProvider { get; set; } = "Baidu";
        public string BaiduTranslationAppId { get; set; } = "";
        public string BaiduTranslationAppSecret { get; set; } = "";
        public string YoudaoTranslationAppKey { get; set; } = "";
        public string YoudaoTranslationAppSecret { get; set; } = "";
        public System.Collections.Generic.List<TranslationProviderProfile> TranslationProviders { get; set; } = [];
        public string SelectedTranslationProviderId { get; set; } = "";
        public System.Collections.Generic.List<string> QuickTranslateDomains { get; set; } = [TranslationDomainCatalog.General];
        public string SelectedQuickTranslateDomain { get; set; } = TranslationDomainCatalog.General;
        public string QuickTranslateTargetLanguage { get; set; } = QuickTranslateTargetLanguages.Default;
        public bool EnableQuickTranslateAiPhonetic { get; set; } = false;
        public string QuickTranslateHotkey { get; set; } = "Alt+Shift+T";
        public string FloatingLyricsHotkey { get; set; } = "";
        public string DesktopWidgetHotkey { get; set; } = "";
        public string WaterReminderDrinkHotkey { get; set; } = "";
        public string QuickTranslateWindowMaterial { get; set; } = "Mica";
        public string QuickTranslateFontFamily { get; set; } = "Microsoft YaHei UI";
        public bool EnableWaterReminder { get; set; } = false;
        public int WaterReminderIntervalMinutes { get; set; } = 45;
        public int WaterReminderSnoozeMinutes { get; set; } = 10;
        public int WaterReminderDailyGoal { get; set; } = 8;
        public bool WaterReminderShowSystemNotification { get; set; } = true;
        public bool WaterReminderHideInFullscreen { get; set; } = true;
        public string WaterReminderQuietStart { get; set; } = "22:00";
        public string WaterReminderQuietEnd { get; set; } = "07:00";
        public string WaterReminderRecordDate { get; set; } = "";
        public int WaterReminderCompletedToday { get; set; } = 0;
        public System.Collections.Generic.List<DateTime> WaterReminderDrinkHistory { get; set; } = [];
        public DateTime? WaterReminderLastCompletedAt { get; set; }
        public DateTime? WaterReminderSnoozedUntil { get; set; }
        public string SettingsWindowMaterial { get; set; } = "Mica";
        public string ApplicationTheme { get; set; } = "System";
        public bool EnableTaskbarPerformanceMonitor { get; set; } = false;
        public int TaskbarPerformanceSummaryMetricCount { get; set; } = 5;
        public bool EnableEnhancedTemperatureSensors { get; set; } = false;
        public System.Collections.Generic.List<string> TaskbarPerformanceMetrics { get; set; } =
            TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
        public System.Collections.Generic.List<string> TaskbarPerformanceSummaryMetrics { get; set; } =
            TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
        public int TaskbarPerformanceRefreshSeconds { get; set; } = 1;
        public bool TaskbarPerformanceIsDoubleLine { get; set; } = false;
        public string TaskbarPerformanceFontFamily { get; set; } = "Microsoft YaHei";
        public double TaskbarPerformanceFontSize { get; set; } = 10;
        public string TaskbarPerformanceFontWeight { get; set; } = "SemiBold";
        public double FontSize { get; set; } = 12;
        public string FontFamily { get; set; } = "Microsoft YaHei";
        public string TextColor { get; set; } = "#FFFFFF"; // Hex code
        public string ActiveTextColor { get; set; } = "#FF33BBFF"; // Highlight color for played lyrics
        public string BackgroundColor { get; set; } = "#33000000"; // Hex code
        public bool EnableShadow { get; set; } = false;
        public string FontWeight { get; set; } = "SemiBold"; // Normal, SemiBold, Bold etc.
        public bool EnableOutline { get; set; } = false; // Simulated outline
        public bool EnableTaskbarHoverMediaControls { get; set; } = true;

        public int OffsetX { get; set; } = 10;
        public int? TaskbarPerformanceOffsetX { get; set; }
        public bool EnableTaskbarTranslateButton { get; set; } = true;
        public int? TaskbarTranslateButtonOffsetX { get; set; }
        public int? TaskbarWaterReminderOffsetX { get; set; }
        public string TaskbarMonitorDeviceName { get; set; } = "";
        public string TaskbarPerformanceMonitorDeviceName { get; set; } = "";
        public string TaskbarTranslateButtonMonitorDeviceName { get; set; } = "";
        public string TaskbarWaterReminderMonitorDeviceName { get; set; } = "";
        public bool IsDoubleLine { get; set; } = true; 
        public double LyricOffsetSeconds { get; set; } = 0; 
        public System.Collections.Generic.List<string> IncludedAppIds { get; set; } = new System.Collections.Generic.List<string>();  
        public bool EnableFloatingLyrics { get; set; } = false; 
        public bool FloatingLyricsLocked { get; set; } = false; 
        public bool FloatingLyricsClickThrough { get; set; } = false; 
        public string FloatingLyricsFontFamily { get; set; } = "Microsoft YaHei";
        public double FloatingLyricsFontSize { get; set; } = 20;
        public string FloatingLyricsFontWeight { get; set; } = "Bold";
        public string FloatingLyricsTextColor { get; set; } = "#FF1F2937";
        public string FloatingLyricsBackgroundColor { get; set; } = "#FFFFFFFF";
        public bool FloatingLyricsUseAcrylic { get; set; } = false;
        public bool FloatingLyricsEnableShadow { get; set; } = false;
        public double? FloatingLyricsLeft { get; set; } = null;
        public double? FloatingLyricsTop { get; set; } = null;
        public double? FloatingLyricsWidth { get; set; } = null;
        public string FloatingLyricsMonitorDeviceName { get; set; } = "";

        public bool EnableDesktopWidget { get; set; } = false;
        public DesktopWidgetTheme DesktopWidgetTheme { get; set; } = DesktopWidgetTheme.Dark;
        public double DesktopWidgetLeft { get; set; } = 48;
        public double DesktopWidgetTop { get; set; } = 48;
        public string DesktopWidgetMonitorDeviceName { get; set; } = "";
        public double? DesktopWidgetMonitorOffsetX { get; set; } = null;
        public double? DesktopWidgetMonitorOffsetY { get; set; } = null;
        public bool DesktopWidgetLocked { get; set; } = false;
        
        public double NextLyricFontSizeDiff { get; set; } = 2.0;
        public double DoubleLineGap { get; set; } = 0.0;
        public string NextLyricFontWeight { get; set; } = "Normal"; // Normal, Light, Bold etc. 

        public bool RunOnlyWithMusicApp { get; set; } = false;
        public string MusicAppProcessNames { get; set; } = "QQMusic,cloudmusic,Spotify,YesPlayMusic,Foobar2000"; 
        public bool CenterTaskbarIcons { get; set; } = false;
        public bool ApplyTaskbarAcrylic { get; set; } = false;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool LaunchOnStartup { get; set; } = false;

        public static string SettingsPath => SettingsStorage.CurrentPath;

        public static string GetSettingsPath(string localApplicationDataPath) => SettingsStorage.GetPath(localApplicationDataPath);

        public static AppSettings Load()
        {
            try
            {
                string configPath = SettingsPath;
                MigrateLegacySettingsIfNeeded(configPath);
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using JsonDocument document = JsonDocument.Parse(json);
                    bool hasSummaryMetrics = document.RootElement.TryGetProperty(
                        nameof(TaskbarPerformanceSummaryMetrics), out _);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings == null) return new AppSettings();
                    settings.TranslationProviders = TranslationProviderProfiles.Normalize(
                        settings.TranslationProviders,
                        settings.TranslationProvider,
                        settings.BaiduTranslationAppId,
                        settings.BaiduTranslationAppSecret,
                        settings.YoudaoTranslationAppKey,
                        settings.YoudaoTranslationAppSecret);
                    settings.SelectedTranslationProviderId = TranslationProviderProfiles.ResolveSelectedId(
                        settings.TranslationProviders,
                        settings.SelectedTranslationProviderId);
                    settings.QuickTranslateDomains = TranslationDomainCatalog.Normalize(settings.QuickTranslateDomains);
                    settings.SelectedQuickTranslateDomain = TranslationDomainCatalog.ResolveSelected(
                        settings.QuickTranslateDomains,
                        settings.SelectedQuickTranslateDomain);
                    settings.QuickTranslateTargetLanguage = QuickTranslateTargetLanguages.Normalize(
                        settings.QuickTranslateTargetLanguage);
                    WaterReminderSchedule.Normalize(settings, DateTime.Now);
                    settings.TaskbarPerformanceMetrics ??= TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
                    settings.TaskbarPerformanceMetrics = TaskbarPerformanceMetricCatalog.Normalize(settings.TaskbarPerformanceMetrics);
                    settings.TaskbarPerformanceSummaryMetricCount = Math.Clamp(
                        settings.TaskbarPerformanceSummaryMetricCount,
                        1,
                        TaskbarPerformanceMetricCatalog.Definitions.Count);
                    if (!hasSummaryMetrics)
                    {
                        settings.TaskbarPerformanceSummaryMetrics = TaskbarPerformanceMetricCatalog.GetSummarySelection(
                            settings.TaskbarPerformanceMetrics,
                            settings.TaskbarPerformanceSummaryMetricCount);
                    }
                    settings.TaskbarPerformanceSummaryMetrics = TaskbarPerformanceMetricCatalog.GetSummarySelection(
                        settings.TaskbarPerformanceMetrics,
                        settings.TaskbarPerformanceSummaryMetrics,
                        settings.TaskbarPerformanceSummaryMetricCount);
                    settings.TaskbarPerformanceRefreshSeconds = NormalizeRefreshSeconds(settings.TaskbarPerformanceRefreshSeconds);
                    settings.TaskbarPerformanceMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                        settings.TaskbarPerformanceMonitorDeviceName,
                        settings.TaskbarMonitorDeviceName);
                    settings.TaskbarTranslateButtonMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                        settings.TaskbarTranslateButtonMonitorDeviceName,
                        settings.TaskbarMonitorDeviceName);
                    settings.TaskbarWaterReminderMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                        settings.TaskbarWaterReminderMonitorDeviceName,
                        settings.TaskbarMonitorDeviceName);
                    return settings;
                }
            }
            catch { }
            return new AppSettings();
        }

        public bool Save()
        {
            return Save(out _);
        }

        public bool Save(out string? errorMessage)
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                string configPath = SettingsPath;
                string? directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(configPath, json);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public AppSettings Clone()
        {
            var clone = (AppSettings)this.MemberwiseClone();
            clone.IncludedAppIds = new System.Collections.Generic.List<string>(IncludedAppIds);
            clone.TaskbarPerformanceMetrics = new System.Collections.Generic.List<string>(TaskbarPerformanceMetrics);
            clone.TaskbarPerformanceSummaryMetrics = new System.Collections.Generic.List<string>(TaskbarPerformanceSummaryMetrics);
            clone.QuickTranslateDomains = new System.Collections.Generic.List<string>(QuickTranslateDomains);
            clone.WaterReminderDrinkHistory = new System.Collections.Generic.List<DateTime>(WaterReminderDrinkHistory);
            clone.TranslationProviders = TranslationProviders.Select(profile => new TranslationProviderProfile
            {
                Id = profile.Id,
                DisplayName = profile.DisplayName,
                Provider = profile.Provider,
                AppId = profile.AppId,
                AppSecret = profile.AppSecret,
                ExtraCredential = profile.ExtraCredential,
                ApiBaseUrl = profile.ApiBaseUrl,
                SystemPrompt = profile.SystemPrompt
            }).ToList();
            return clone;
        }

        private static int NormalizeRefreshSeconds(int value) => value is 1 or 2 or 5 ? value : 1;

        private static void MigrateLegacySettingsIfNeeded(string destinationPath)
        {
            if (File.Exists(destinationPath)) return;

            foreach (string sourcePath in GetLegacySettingsPaths(destinationPath))
            {
                try
                {
                    string json = File.ReadAllText(sourcePath);
                    using JsonDocument _ = JsonDocument.Parse(json);

                    string? directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(destinationPath, json);
                    return;
                }
                catch
                {
                    // Ignore stale or malformed development-build configuration files.
                }
            }
        }

        private static IEnumerable<string> GetLegacySettingsPaths(string destinationPath)
        {
            string currentBuildPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (!PathEquals(currentBuildPath, destinationPath)) yield return currentBuildPath;

            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !string.Equals(directory.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Parent;
            }

            if (directory == null) yield break;

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(directory.FullName, "settings.json", SearchOption.AllDirectories)
                    .Where(path => !PathEquals(path, destinationPath))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();
            }
            catch
            {
                yield break;
            }

            foreach (string candidate in candidates)
            {
                if (!PathEquals(candidate, currentBuildPath)) yield return candidate;
            }
        }

        private static bool PathEquals(string first, string second) => string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
    }
}

