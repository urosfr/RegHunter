using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace RegHunter
{
    internal enum RegOp { Added, Changed, Removed }

    internal sealed class RegEventArgs : EventArgs
    {
        public DateTime Time;
        public RegOp Op;
        public string KeyPath;
        public string Detail;
    }

    internal sealed class RegMonitor : IDisposable
    {
        // All PIDs belonging to the target process group (same exe name)
        private readonly ConcurrentDictionary<int, bool> _targetPids =
            new ConcurrentDictionary<int, bool>();

        private readonly int _primaryPid;
        private readonly string _processName;

        private TraceEventSession _session;
        private Thread _processThread;
        private volatile bool _running;

        private readonly ConcurrentDictionary<ulong, string> _kcbMap =
            new ConcurrentDictionary<ulong, string>();

        private readonly string _currentUserSid;

        private readonly ConcurrentQueue<RegEventArgs> _pending =
            new ConcurrentQueue<RegEventArgs>();

        private System.Timers.Timer _flushTimer;

        public event EventHandler<RegEventArgs> RegistryEvent;
        public event EventHandler<IList<RegEventArgs>> BatchReady;

        public RegMonitor(int pid)
        {
            _primaryPid = pid;

            try
            {
                var identity = WindowsIdentity.GetCurrent();
                _currentUserSid = identity.User?.Value ?? "";
            }
            catch { _currentUserSid = ""; }

            // Seed all PIDs that share the same process name
            try
            {
                var primary = Process.GetProcessById(pid);
                _processName = primary.ProcessName;

                foreach (var p in Process.GetProcessesByName(_processName))
                {
                    _targetPids[p.Id] = true;
                    p.Dispose();
                }
            }
            catch
            {
                _targetPids[pid] = true;
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            string sessionName = KernelTraceEventParser.KernelSessionName;
            try { TraceEventSession.GetActiveSession(sessionName)?.Stop(); } catch { }

            _session = new TraceEventSession(sessionName, null) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Registry |
                KernelTraceEventParser.Keywords.Thread |
                KernelTraceEventParser.Keywords.Process);

            // KCB map
            _session.Source.Kernel.RegistryKCBCreate += e => CacheKcb(e);
            _session.Source.Kernel.RegistryKCBDelete += e => _kcbMap.TryRemove((ulong)e.KeyHandle, out _);
            _session.Source.Kernel.RegistryKCBRundownBegin += e => CacheKcb(e);
            _session.Source.Kernel.RegistryKCBRundownEnd += e => CacheKcb(e);

            // Track new processes with same name (e.g. spawned workers)
            _session.Source.Kernel.ProcessStart += e =>
            {
                if (!string.IsNullOrEmpty(_processName) &&
                    string.Equals(e.ImageFileName, _processName, StringComparison.OrdinalIgnoreCase))
                    _targetPids[e.ProcessID] = true;
            };
            _session.Source.Kernel.ProcessStop += e =>
                _targetPids.TryRemove(e.ProcessID, out _);

            // Registry events
            _session.Source.Kernel.RegistryCreate += e => Emit(e, RegOp.Added, BuildKeyPath(e), "");
            _session.Source.Kernel.RegistryDelete += e => Emit(e, RegOp.Removed, BuildKeyPath(e), "");
            _session.Source.Kernel.RegistrySetInformation += e => Emit(e, RegOp.Changed, BuildKeyPath(e), "set info");
            _session.Source.Kernel.RegistrySetValue += e => Emit(e, RegOp.Changed, BuildKeyPath(e), "value: " + e.ValueName);
            _session.Source.Kernel.RegistryDeleteValue += e => Emit(e, RegOp.Changed, BuildKeyPath(e), "deleted value: " + e.ValueName);

            _flushTimer = new System.Timers.Timer(50) { AutoReset = true };
            _flushTimer.Elapsed += (s, ev) => FlushBatch();
            _flushTimer.Start();

            _processThread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch { }
            })
            { IsBackground = true, Name = "RegMonitor-ETW" };
            _processThread.Start();
        }

        private void CacheKcb(RegistryTraceData e)
        {
            if (!string.IsNullOrEmpty(e.KeyName))
                _kcbMap[(ulong)e.KeyHandle] = e.KeyName;
        }

        private string BuildKeyPath(RegistryTraceData e)
        {
            string basePath = "";
            if ((ulong)e.KeyHandle != 0 && _kcbMap.TryGetValue((ulong)e.KeyHandle, out var cached))
                basePath = cached;

            string suffix = e.KeyName ?? "";
            string full;
            if (string.IsNullOrEmpty(basePath))
                full = string.IsNullOrEmpty(suffix) ? "(unresolved)" : suffix;
            else if (string.IsNullOrEmpty(suffix))
                full = basePath;
            else
                full = basePath.TrimEnd('\\') + "\\" + suffix.TrimStart('\\');

            return KernelPathToFriendly(full);
        }

        private string KernelPathToFriendly(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string upper = path.ToUpperInvariant();

            const string machinePrefix = @"\REGISTRY\MACHINE";
            const string userPrefix = @"\REGISTRY\USER\";

            if (upper.StartsWith(machinePrefix.ToUpperInvariant()))
            {
                string rest = path.Length > machinePrefix.Length
                    ? path.Substring(machinePrefix.Length).TrimStart('\\') : "";
                return string.IsNullOrEmpty(rest) ? "HKLM" : @"HKLM\" + rest;
            }

            if (upper.StartsWith(userPrefix.ToUpperInvariant()))
            {
                string rest = path.Substring(userPrefix.Length);
                int sep = rest.IndexOf('\\');
                string sid = sep < 0 ? rest : rest.Substring(0, sep);
                string tail = sep < 0 ? "" : rest.Substring(sep + 1);

                bool isClasses = sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);
                string bareSid = isClasses ? sid.Substring(0, sid.Length - "_Classes".Length) : sid;
                bool isCurrentUser = !string.IsNullOrEmpty(_currentUserSid) &&
                    string.Equals(bareSid, _currentUserSid, StringComparison.OrdinalIgnoreCase);

                if (isClasses)
                    return isCurrentUser
                        ? (string.IsNullOrEmpty(tail) ? "HKCR" : @"HKCR\" + tail)
                        : (string.IsNullOrEmpty(tail) ? $@"HKU\{sid}" : $@"HKU\{sid}\{tail}");

                if (isCurrentUser)
                    return string.IsNullOrEmpty(tail) ? "HKCU" : @"HKCU\" + tail;

                return string.IsNullOrEmpty(tail) ? $@"HKU\{sid}" : $@"HKU\{sid}\{tail}";
            }

            return path;
        }

        private void Emit(RegistryTraceData e, RegOp op, string keyPath, string detail)
        {
            if (!_targetPids.ContainsKey(e.ProcessID)) return;

            var args = new RegEventArgs
            {
                Time = e.TimeStamp,
                Op = op,
                KeyPath = keyPath,
                Detail = detail
            };
            _pending.Enqueue(args);
            RegistryEvent?.Invoke(this, args);
        }

        private void FlushBatch()
        {
            if (_pending.IsEmpty) return;
            var batch = new List<RegEventArgs>();
            while (_pending.TryDequeue(out var item))
                batch.Add(item);
            if (batch.Count > 0)
                BatchReady?.Invoke(this, batch);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
            try { _session?.Stop(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _session?.Dispose();
        }
    }
}