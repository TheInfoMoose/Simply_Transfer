using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimplyTransfer.Core.Models;

namespace SimplyTransfer.Core.Services
{
    public class TelemetryPacket
    {
        public string Action { get; set; } = string.Empty;
        public string CurrentFile { get; set; } = string.Empty;
        public double ProgressPercentage { get; set; }
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double TransferSpeedBps { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public int ItemIndex { get; set; }
        public int TotalItems { get; set; }
    }

    /// <summary>
    /// Listens for incoming UDP telemetry packets from the Source to display in Destination Mode.
    /// </summary>
    public class TelemetryListenerService : IDisposable
    {
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private readonly int _port;

        public event EventHandler<TelemetryPacket>? TelemetryReceived;

        public TelemetryListenerService(int port = 55555)
        {
            _port = port;
        }

        public void StartListening()
        {
            if (_udpClient != null) return;

            try
            {
                _udpClient = new UdpClient(_port);
                _cts = new CancellationTokenSource();
                
                _ = Task.Run(async () =>
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var result = await _udpClient.ReceiveAsync(_cts.Token);
                            string json = Encoding.UTF8.GetString(result.Buffer);
                            var packet = JsonSerializer.Deserialize<TelemetryPacket>(json);
                            
                            if (packet != null)
                            {
                                TelemetryReceived?.Invoke(this, packet);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception) { /* Ignore parsing or socket errors */ }
                    }
                }, _cts.Token);
            }
            catch (Exception)
            {
                // Port might be in use
            }
        }

        public void StopListening()
        {
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }

        public void Dispose()
        {
            StopListening();
            _cts?.Dispose();
        }
    }

    /// <summary>
    /// Sends telemetry packets to a specified remote endpoint.
    /// </summary>
    public class TelemetrySenderService : IDisposable
    {
        private readonly UdpClient _udpClient;
        private IPEndPoint? _targetEndPoint;

        public TelemetrySenderService(string targetHost, int port = 55555)
        {
            _udpClient = new UdpClient();
            if (IPAddress.TryParse(targetHost, out var ipAddress))
            {
                _targetEndPoint = new IPEndPoint(ipAddress, port);
            }
            else
            {
                // Defer DNS resolution to prevent UI stalls and handle failures gracefully
                _ = ResolveHostAsync(targetHost, port);
            }
        }

        private async Task ResolveHostAsync(string targetHost, int port)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(targetHost);
                if (addresses != null && addresses.Length > 0)
                {
                    _targetEndPoint = new IPEndPoint(addresses[0], port);
                }
            }
            catch { /* Ignore DNS failures */ }
        }

        public void SendTelemetry(TelemetryPacket packet)
        {
            if (_targetEndPoint == null) return;

            try
            {
                string json = JsonSerializer.Serialize(packet);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                _udpClient.SendAsync(buffer, buffer.Length, _targetEndPoint);
            }
            catch { /* Fire and forget, don't crash the sync */ }
        }

        public void Dispose()
        {
            _udpClient.Dispose();
        }
    }
}
