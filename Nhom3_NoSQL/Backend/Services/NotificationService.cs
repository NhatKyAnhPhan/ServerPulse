using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Nhom3_NoSQL.Backend.Interfaces;
using Nhom3_NoSQL.Backend.Models;

namespace Nhom3_NoSQL.Backend.Services
{
    //Gửi cảnh báo tới khay hệ thống Windows và lưu bản ghi ngoài MongoDB.
    public class NotificationService : INotificationService, IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly string _logPath;
        private readonly object _sync = new object();

        public NotificationService(bool showPopup = true, string logPath = null)
        {
            _logPath = logPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ServerPulse", "logs", "alerts.log");
            if (showPopup)
            {
                _icon = new NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = "ServerPulse",
                    Visible = true
                };
            }
        }

        public Task NotifyAsync(AlertEvent alertEvent)
        {
            if (alertEvent == null) throw new ArgumentNullException(nameof(alertEvent));
            var message = (alertEvent.Message ?? "").Replace("\r", " ").Replace("\n", " ");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\t{alertEvent.Severity}\t{alertEvent.Code}\t{alertEvent.ServerName}\t{message}{Environment.NewLine}";
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, line, new UTF8Encoding(false));
            }
            if (_icon != null)
            {
                var title = $"ServerPulse · {alertEvent.Severity}";
                var body = $"{alertEvent.ServerName}: {message}";
                _icon.ShowBalloonTip(10000, title, body, alertEvent.Severity == AlertSeverity.Critical
                    ? ToolTipIcon.Error : ToolTipIcon.Warning);
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_icon == null) return;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}