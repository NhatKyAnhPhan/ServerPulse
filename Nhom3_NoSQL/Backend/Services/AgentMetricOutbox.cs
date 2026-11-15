using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Nhom3_NoSQL.Backend.Models;

namespace Nhom3_NoSQL.Backend.Services
{
    public sealed class AgentMetricOutbox
    {
        const int MaxPendingMetrics = 10000;
        readonly string _path;
        readonly List<MetricSnapshot> _pending;
        readonly object _sync = new object();

        public AgentMetricOutbox(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _pending = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<MetricSnapshot>>(File.ReadAllText(_path)) ?? new List<MetricSnapshot>()
                : new List<MetricSnapshot>();
        }

        public void Enqueue(MetricSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (_sync)
            {
                if (_pending.Count >= MaxPendingMetrics)
                    throw new InvalidOperationException("Hàng đợi Agent đã đầy; không thể lưu thêm metric.");
                _pending.Add(snapshot);
                try { Persist(); }
                catch
                {
                    _pending.RemoveAt(_pending.Count - 1);
                    throw;
                }
            }
        }

        public bool TryPeek(out MetricSnapshot snapshot)
        {
            lock (_sync)
            {
                snapshot = _pending.Count == 0 ? null : _pending[0];
                return snapshot != null;
            }
        }

        public void RemoveHead()
        {
            lock (_sync)
            {
                if (_pending.Count == 0) throw new InvalidOperationException("Hàng đợi Agent không có metric để xác nhận.");
                var head = _pending[0];
                _pending.RemoveAt(0);
                try { Persist(); }
                catch
                {
                    _pending.Insert(0, head);
                    throw;
                }
            }
        }

        private void Persist()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_pending), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
            else File.Move(temporaryPath, _path);
        }
    }
}
