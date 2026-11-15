using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                // Đã có ServerPulse khác chạy (cổng 5080 đang dùng) -> chỉ mở giao diện, kết nối vào server đó
                if (!args.Contains("--headless") && !args.Contains("--screenshots") && ApiAlreadyRunning())
                {
                    RunUi(new ApiClient(ApiUrl));
                    return;
                }
                Run(args);
            }
            catch (Exception ex)
            {
                var log = GetLogPath("startup-error.log");
                File.WriteAllText(log, DateTime.Now + Environment.NewLine + ex);
                MessageBox.Show(ex.Message + "\n\nChi tiết: " + log, "ServerPulse - lỗi khởi động", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static bool ApiAlreadyRunning()
        {
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                    return tcp.ConnectAsync("localhost", 5080).Wait(500) && tcp.Connected;
            }
            catch { return false; }
        }

        static void ReportError(Exception ex)
        {
            if (ex == null) return;
            var log = GetLogPath("error.log");
            try { File.AppendAllText(log, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine); } catch { }
            MessageBox.Show(ex.Message + "\n\nChi tiết: " + log, "ServerPulse - lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static string GetLogPath(string fileName)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerPulse", "logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
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
            string diskDrive;
            bool notificationPopup = true;
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                mongoConn = ReadRequiredSetting(settings.RootElement, "MongoDb", "ConnectionString", settingsPath);
                mongoDbName = ReadRequiredSetting(settings.RootElement, "MongoDb", "Database", settingsPath);
                redisConn = ReadOptionalSetting(settings.RootElement, "Redis", "ConnectionString", settingsPath);
                elasticUri = ReadOptionalSetting(settings.RootElement, "Elasticsearch", "Uri", settingsPath);
                appMode = ReadRequiredSetting(settings.RootElement, "App", "Mode", settingsPath);
                diskDrive = ReadRequiredSetting(settings.RootElement, "Collector", "DiskDrive", settingsPath);
                notificationPopup = ReadOptionalBooleanSetting(settings.RootElement, "Notifications", "PopupEnabled", settingsPath, true);
            }
            if (!string.Equals(appMode, "Demo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(appMode, "Real", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cấu hình 'App:Mode' chỉ nhận giá trị 'Demo' hoặc 'Real'.");
            var demoMode = string.Equals(appMode, "Demo", StringComparison.OrdinalIgnoreCase);

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
            Api = new ApiServer(ApiUrl, MongoContext, ServerRepo, MetricRepo, AlertRepo, EventRepo, Auth, AlertEngine);
            Api.Start();

            var shot = Array.IndexOf(args, "--screenshots");
            if (args.Contains("--headless"))
                Application.Run(); // chỉ chạy API server + Collector, không mở giao diện (dừng bằng Task Manager)
            else if (shot >= 0)
                ScreenshotRunner.Run(args.Length > shot + 1 ? args[shot + 1] : "screenshots"); // tự chụp ảnh các màn hình cho báo cáo
            else
                RunUi(new ApiClient(ApiUrl));
            Api.Stop();
            MetricCollector.Dispose();
            if (NotificationService is IDisposable) ((IDisposable)NotificationService).Dispose();
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
