using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nhom3_NoSQL.Backend.Interfaces;
using Nhom3_NoSQL.Backend.Models;

namespace Nhom3_NoSQL.Backend.Services
{
    //Chạy nền, định kỳ đo hiệu năng máy hiện tại và đẩy vào Redis + Mongo
    public class MetricCollectorService : IDisposable
    {
        private readonly ICacheService _cache; // có thể null nếu không chạy Redis
        private readonly IMetricRepository _metricRepo;
        private readonly AlertEngineService _alertEngine;
        private readonly LocalMetricSampler _sampler;
        private readonly Func<MetricSnapshot, Task> _remoteSink;
        private readonly Action<Exception> _onError;
        private int _started;
        private CancellationTokenSource _stop;
        private Task _worker;
        private AgentMetricOutbox _outbox;

        public MetricCollectorService(ICacheService cache, IMetricRepository metricRepo, AlertEngineService alertEngine, string diskDrive)
        {
            _cache = cache;
            _metricRepo = metricRepo;
            _alertEngine = alertEngine;
            _sampler = new LocalMetricSampler(diskDrive);
        }

        public MetricCollectorService(string diskDrive, Func<MetricSnapshot, Task> remoteSink, Action<Exception> onError)
        {
            _sampler = new LocalMetricSampler(diskDrive);
            _remoteSink = remoteSink ?? throw new ArgumentNullException(nameof(remoteSink));
            _onError = onError;
        }

        public void Start(string serverId, string serverName, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("Metric collector đã được khởi động.");

            _stop = new CancellationTokenSource();
            if (_remoteSink != null)
            {
                _outbox = new AgentMetricOutbox(GetOutboxPath(serverId));
                _worker = RunAgentAsync(serverId, interval, _stop.Token);
            }
            else
                _worker = RunLocalAsync(serverId, serverName, interval, _stop.Token);
        }

        private async Task RunLocalAsync(string serverId, string serverName, TimeSpan interval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await CollectOnceAsync(serverId, serverName); }
                catch (Exception ex) { ReportError(ex); }
                try { await Task.Delay(interval, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }

        private async Task RunAgentAsync(string serverId, TimeSpan interval, CancellationToken cancellationToken)
        {
            var sampleTask = SampleAgentAsync(serverId, interval, cancellationToken);
            var sendTask = DrainAgentQueueAsync(cancellationToken);
            await Task.WhenAll(sampleTask, sendTask);
        }

        private async Task SampleAgentAsync(string serverId, TimeSpan interval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { _outbox.Enqueue(_sampler.Sample(serverId)); }
                catch (Exception ex) { ReportError(ex); }
                try { await Task.Delay(interval, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }

        private async Task DrainAgentQueueAsync(CancellationToken cancellationToken)
        {
            var backoff = TimeSpan.FromSeconds(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                MetricSnapshot snapshot;
                if (!_outbox.TryPeek(out snapshot))
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    continue;
                }

                try
                {
                    await _remoteSink(snapshot);
                    _outbox.RemoveHead();
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                    try { await Task.Delay(backoff, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                }
            }
        }

        private void ReportError(Exception ex)
        {
            if (_onError != null) _onError(ex);
            else Debug.WriteLine("[Collector] " + ex.Message);
        }

        private static string GetOutboxPath(string serverId)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var key = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(serverId ?? "")))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ServerPulse", "queue", key + ".json");
            }
        }

        private async Task CollectOnceAsync(string serverId, string serverName)
        {
            var snapshot = _sampler.Sample(serverId);
            if (_remoteSink != null)
            {
                await _remoteSink(snapshot);
                return;
            }

            // 1) Cache real-time
            if (_cache != null)
            {
                try
                {
                    await _cache.SetLatestMetricAsync(serverId, snapshot);
                    await _cache.PublishAsync($"metrics:{serverId}", JsonSerializer.Serialize(snapshot));
                }
                catch { /* Redis không bắt buộc */ }
            }

            // 2) Lưu lâu dài
            await _metricRepo.InsertAsync(snapshot);

            // 3) Kiểm tra ngưỡng cảnh báo
            await _alertEngine.EvaluateAsync(snapshot, serverName);
        }

        public void Dispose()
        {
            _stop?.Cancel();
            if (_worker != null) _worker.Wait(TimeSpan.FromSeconds(5));
            _sampler.Dispose();
            _stop?.Dispose();
        }
    }
}
