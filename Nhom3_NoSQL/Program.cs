using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using MongoDB.Driver;
using Nhom3_NoSQL.Backend.Api;
using Nhom3_NoSQL.Backend.Interfaces;
using Nhom3_NoSQL.Backend.Models;
using Nhom3_NoSQL.Backend.Services;
using Nhom3_NoSQL.Database;
using Nhom3_NoSQL.Frontend;

namespace Nhom3_NoSQL
{
    internal static class Program
    {
        public static MongoDbContext MongoContext { get; private set; }
        public static ICacheService CacheService { get; private set; }
        public static ISearchService SearchService { get; private set; }
        public static IServerRepository ServerRepo { get; private set; }
        public static IMetricRepository MetricRepo { get; private set; }
        public static IAlertRepository AlertRepo { get; private set; }
        public static IEventRepository EventRepo { get; private set; }
        public static IUserRepository UserRepo { get; private set; }
        public static INotificationService NotificationService { get; private set; }
        public static AlertEngineService AlertEngine { get; private set; }
        public static MetricCollectorService MetricCollector { get; private set; }
        public static AuthService Auth { get; private set; }
        public static ApiServer Api { get; private set; }
        public static DemoDataSeeder Seeder { get; private set; }

        public const string ApiUrl = "http://localhost:5080/";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Lỗi chưa bắt khi đang chạy (vd trong handler async của nút) -> ghi error.log và báo lỗi, không bị "nuốt" im lặng
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportError(e.ExceptionObject as Exception);
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                var log = GetLogPath("startup-error.log");
                AppendRotatingLog(log, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
                MessageBox.Show(ex.Message + "\n\nChi tiết: " + log, "ServerPulse - lỗi khởi động", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static bool ApiAlreadyRunning(string apiBaseUrl)
        {
            try
            {
                var uri = new Uri(apiBaseUrl);
                using (var tcp = new System.Net.Sockets.TcpClient())
                    return tcp.ConnectAsync(uri.DnsSafeHost, uri.Port).Wait(500) && tcp.Connected;
            }
            catch { return false; }
        }

        static void ReportError(Exception ex)
        {
            if (ex == null) return;
            var log = GetLogPath("error.log");
            try { AppendRotatingLog(log, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine); } catch { }
            MessageBox.Show(ex.Message + "\n\nChi tiết: " + log, "ServerPulse - lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static string GetLogPath(string fileName)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerPulse", "logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }

        static void AppendRotatingLog(string path, string entry)
        {
            const long maxBytes = 1024 * 1024;
            if (File.Exists(path) && new FileInfo(path).Length + System.Text.Encoding.UTF8.GetByteCount(entry) > maxBytes)
            {
                var backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
            File.AppendAllText(path, entry);
        }

        static void Run(string[] args)
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Frontend", "appsettings.json");
            if (!File.Exists(settingsPath))
                throw new FileNotFoundException("Không tìm thấy file cấu hình Frontend\\appsettings.json.", settingsPath);

            string mongoConn;
            string mongoDbName;
            string redisConn;
            string elasticUri;
            string appMode;
            string appRole;
            string apiListenUrl;
            string apiBaseUrl;
            string diskDrive;
            string agentHostId;
            int agentIntervalSeconds;
            bool notificationPopup = true;
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                mongoConn = ReadRequiredSetting(settings.RootElement, "MongoDb", "ConnectionString", settingsPath);
                mongoDbName = ReadRequiredSetting(settings.RootElement, "MongoDb", "Database", settingsPath);
                redisConn = ReadOptionalSetting(settings.RootElement, "Redis", "ConnectionString", settingsPath);
                elasticUri = ReadOptionalSetting(settings.RootElement, "Elasticsearch", "Uri", settingsPath);
                appMode = ReadRequiredSetting(settings.RootElement, "App", "Mode", settingsPath);
                appRole = ReadOptionalSetting(settings.RootElement, "App", "Role", settingsPath) ?? "Server";
                apiListenUrl = ReadOptionalSetting(settings.RootElement, "Api", "ListenUrl", settingsPath) ?? ApiUrl;
                apiBaseUrl = ReadOptionalSetting(settings.RootElement, "Api", "BaseUrl", settingsPath) ?? apiListenUrl.TrimEnd('/');
                diskDrive = ReadRequiredSetting(settings.RootElement, "Collector", "DiskDrive", settingsPath);
                agentHostId = ReadOptionalSetting(settings.RootElement, "Agent", "ServerId", settingsPath);
                agentIntervalSeconds = ReadOptionalIntSetting(settings.RootElement, "Agent", "IntervalSeconds", settingsPath, 5);
                notificationPopup = ReadOptionalBooleanSetting(settings.RootElement, "Notifications", "PopupEnabled", settingsPath, true);
            }
            if (agentIntervalSeconds < 2 || agentIntervalSeconds > 300)
                throw new InvalidDataException("Cấu hình 'Agent:IntervalSeconds' phải từ 2 đến 300.");

            if (args.Contains("--agent"))
            {
                RunAgent(apiBaseUrl, diskDrive, agentHostId, agentIntervalSeconds);
                return;
            }

            if (!string.Equals(appMode, "Demo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(appMode, "Real", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cấu hình 'App:Mode' chỉ nhận giá trị 'Demo' hoặc 'Real'.");
            var demoMode = string.Equals(appMode, "Demo", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(appRole, "Client", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRemoteUrl(apiBaseUrl, "Api:BaseUrl");
                RunUi(new ApiClient(apiBaseUrl));
                return;
            }
            if (!string.Equals(appRole, "Server", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cấu hình 'App:Role' chỉ nhận giá trị 'Server' hoặc 'Client'.");

            ValidateListenUrl(apiListenUrl);
            ValidateRemoteUrl(apiBaseUrl, "Api:BaseUrl");
            if (!args.Contains("--headless") && !args.Contains("--screenshots") && ApiAlreadyRunning(apiBaseUrl))
            {
                RunUi(new ApiClient(apiBaseUrl));
                return;
            }

            MongoContext = new MongoDbContext(mongoConn, mongoDbName);
            try { MongoContext.EnsureIndexes(); }
            catch (MongoException ex)
            {
                throw new InvalidOperationException("Không kết nối được MongoDB theo cấu hình trong Frontend\\appsettings.json. Hãy kiểm tra MongoDB và ConnectionString.", ex);
            }

            // Redis và Elasticsearch là tùy chọn: không chạy thì hệ thống vẫn hoạt động với MongoDB
            if (!string.IsNullOrWhiteSpace(redisConn))
            {
                try
                {
                    var redis = new RedisCacheService(redisConn);
                    CacheService = redis.IsConnected ? redis : null;
                }
                catch { CacheService = null; }
            }
            SearchService = string.IsNullOrWhiteSpace(elasticUri) ? null : new ElasticSearchService(elasticUri);

            ServerRepo = new MongoServerRepository(MongoContext);
            MetricRepo = new MongoMetricRepository(MongoContext);
            AlertRepo = new MongoAlertRepository(MongoContext);
            EventRepo = new MongoEventRepository(MongoContext);
            UserRepo = new MongoUserRepository(MongoContext);
            NotificationService = new NotificationService(notificationPopup);

            AlertEngine = new AlertEngineService(AlertRepo, MetricRepo, EventRepo, SearchService, NotificationService, CacheService);
            Auth = new AuthService(UserRepo);

            // Chỉ nạp và mô phỏng dữ liệu host mẫu khi App:Mode là Demo
            Seeder = new DemoDataSeeder(MongoContext, AlertEngine);
            Seeder.SeedAsync(
                demoMode,
                Environment.GetEnvironmentVariable("SERVERPULSE_ADMIN_EMAIL"),
                Environment.GetEnvironmentVariable("SERVERPULSE_ADMIN_NAME"),
                Environment.GetEnvironmentVariable("SERVERPULSE_ADMIN_PASSWORD")).GetAwaiter().GetResult();
            Seeder.StartSimulation(demoMode);
            MetricCollector = new MetricCollectorService(CacheService, MetricRepo, AlertEngine, diskDrive);

            // Agent thật cho chính máy đang chạy app (chu kỳ 5 giây)
            var localName = Environment.MachineName.ToLowerInvariant();
            var local = MongoContext.Servers.Find(s => s.Name == localName).FirstOrDefault();
            if (local != null)
                MetricCollector.Start(local.Id, local.Name, TimeSpan.FromSeconds(5));

            // REST API - giao diện WinForms và Agent đều giao tiếp qua đây
            Api = new ApiServer(apiListenUrl, MongoContext, ServerRepo, MetricRepo, AlertRepo, EventRepo, Auth, AlertEngine);
            Api.Start();

            var shot = Array.IndexOf(args, "--screenshots");
            if (args.Contains("--headless"))
                Application.Run(); // chỉ chạy API server + Collector, không mở giao diện (dừng bằng Task Manager)
            else if (shot >= 0)
                ScreenshotRunner.Run(args.Length > shot + 1 ? args[shot + 1] : "screenshots"); // tự chụp ảnh các màn hình cho báo cáo
            else
                RunUi(new ApiClient(apiBaseUrl));
            Api.Stop();
            MetricCollector.Dispose();
            if (NotificationService is IDisposable) ((IDisposable)NotificationService).Dispose();
        }

        static void RunAgent(string apiBaseUrl, string diskDrive, string hostId, int intervalSeconds)
        {
            ValidateRemoteUrl(apiBaseUrl, "Api:BaseUrl");
            var token = Environment.GetEnvironmentVariable("SERVERPULSE_AGENT_TOKEN");
            if (string.IsNullOrWhiteSpace(hostId))
                throw new InvalidDataException("Thiếu cấu hình 'Agent:ServerId'. Hãy sao chép Host ID từ máy chủ trung tâm.");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Thiếu biến môi trường SERVERPULSE_AGENT_TOKEN. Hãy cấp mã Agent cho host và đặt biến này.");

            var client = new ApiClient(apiBaseUrl);
            var collector = new MetricCollectorService(diskDrive, snapshot => client.CollectAsync(new CollectorBody
            {
                ServerId = hostId,
                Cpu = snapshot.CpuUsagePercent,
                Ram = snapshot.RamUsagePercent,
                Disk = snapshot.DiskUsagePercent,
                NetworkInKbps = snapshot.NetworkInKbps,
                NetworkOutKbps = snapshot.NetworkOutKbps
            }, token), ReportAgentError);

            try
            {
                collector.Start(hostId, Environment.MachineName, TimeSpan.FromSeconds(intervalSeconds));
                Application.Run();
            }
            finally { collector.Dispose(); }
        }

        static void ReportAgentError(Exception ex)
        {
            if (ex == null) return;
            var log = GetLogPath("agent-error.log");
            try { AppendRotatingLog(log, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine); }
            catch { Debug.WriteLine("[Agent] " + ex); }
        }

        static void ValidateRemoteUrl(string url, string setting)
        {
            if (url != null && (url.Contains("://+:") || url.Contains("://*:")))
                throw new InvalidDataException($"Cấu hình '{setting}' phải dùng hostname cụ thể, không dùng wildcard.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"Cấu hình '{setting}' phải là URL http hoặc https hợp lệ.");

            var hostIsLocal = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                              (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
            if (!hostIsLocal && uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Cấu hình '{setting}' dùng từ xa bắt buộc phải là HTTPS.");
        }

        static void ValidateListenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Cấu hình 'Api:ListenUrl' phải là URL có dấu / ở cuối.");

            var wildcard = url.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase) ||
                           url.StartsWith("https://+:", StringComparison.OrdinalIgnoreCase) ||
                           url.StartsWith("http://*:", StringComparison.OrdinalIgnoreCase) ||
                           url.StartsWith("https://*:", StringComparison.OrdinalIgnoreCase);
            var normalized = url;
            if (wildcard)
            {
                var authorityStart = url.IndexOf("://", StringComparison.OrdinalIgnoreCase) + 3;
                var portSeparator = url.IndexOf(':', authorityStart);
                if (portSeparator < 0)
                    throw new InvalidDataException("Cấu hình 'Api:ListenUrl' wildcard cần chỉ rõ cổng.");
                normalized = url.Substring(0, authorityStart) + "127.0.0.1" + url.Substring(portSeparator);
            }
            ValidateRemoteUrl(normalized, "Api:ListenUrl");
            if (wildcard && normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Listener wildcard chỉ được dùng với HTTPS.");
        }

        static string ReadRequiredSetting(JsonElement root, string section, string name, string settingsPath)
        {
            JsonElement sectionElement;
            JsonElement valueElement;
            if (!root.TryGetProperty(section, out sectionElement) ||
                sectionElement.ValueKind != JsonValueKind.Object ||
                !sectionElement.TryGetProperty(name, out valueElement) ||
                valueElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(valueElement.GetString()))
                throw new InvalidDataException($"Thiếu cấu hình bắt buộc '{section}:{name}' trong {settingsPath}.");

            return valueElement.GetString();
        }

        static string ReadOptionalSetting(JsonElement root, string section, string name, string settingsPath)
        {
            JsonElement sectionElement;
            if (!root.TryGetProperty(section, out sectionElement))
                return null;
            if (sectionElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Cấu hình '{section}' trong {settingsPath} phải là một đối tượng.");

            JsonElement valueElement;
            if (!sectionElement.TryGetProperty(name, out valueElement))
                return null;
            if (valueElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Cấu hình '{section}:{name}' trong {settingsPath} phải là chuỗi.");

            var value = valueElement.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        static bool ReadOptionalBooleanSetting(JsonElement root, string section, string name, string settingsPath, bool defaultValue)
        {
            JsonElement sectionElement;
            if (!root.TryGetProperty(section, out sectionElement))
                return defaultValue;
            if (sectionElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Cấu hình '{section}' trong {settingsPath} phải là một đối tượng.");

            JsonElement valueElement;
            if (!sectionElement.TryGetProperty(name, out valueElement))
                return defaultValue;
            if (valueElement.ValueKind == JsonValueKind.True) return true;
            if (valueElement.ValueKind == JsonValueKind.False) return false;
            throw new InvalidDataException($"Cấu hình '{section}:{name}' trong {settingsPath} phải là boolean true hoặc false.");
        }

        static int ReadOptionalIntSetting(JsonElement root, string section, string name, string settingsPath, int defaultValue)
        {
            JsonElement sectionElement;
            if (!root.TryGetProperty(section, out sectionElement)) return defaultValue;
            if (sectionElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Cấu hình '{section}' trong {settingsPath} phải là một đối tượng.");
            JsonElement valueElement;
            if (!sectionElement.TryGetProperty(name, out valueElement)) return defaultValue;
            if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetInt32(out var value))
                throw new InvalidDataException($"Cấu hình '{section}:{name}' trong {settingsPath} phải là số nguyên.");
            return value;
        }

        //Đăng nhập -> màn hình chính; đăng xuất thì quay lại màn hình đăng nhập
        static void RunUi(ApiClient client)
        {
            while (true)
            {
                using (var login = new FormLogin(client))
                    if (login.ShowDialog() != DialogResult.OK) return;
                var main = new FormMain(client);
                Application.Run(main);
                if (!main.LoggedOut) return;
            }
        }
    }
}
