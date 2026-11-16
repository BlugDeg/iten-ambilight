using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ItenAmbilight
{
    /// <summary>
    /// Sorgt dafür, dass das Programm pro angemeldetem Benutzer nur einmal läuft.
    /// Ein zweiter Start gibt seine Befehle (z. B. "--toggle" vom Stream Deck) an das laufende Programm weiter und beendet sich.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\ItenAmbilight.SingleInstance";
        private static string PipeName => "ItenAmbilight.Command." + Process.GetCurrentProcess().SessionId;

        private Mutex? _mutex;
        private bool _owned;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        /// <summary>true = dieses Programm ist das einzige (bzw. erste). Muss im selben Thread wie Dispose aufgerufen werden.</summary>
        public bool TryAcquire(int waitMs)
        {
            if (_owned) return true;
            try
            {
                _mutex ??= new Mutex(false, MutexName);
                _owned = _mutex.WaitOne(waitMs);
            }
            catch (AbandonedMutexException) { _owned = true; }   // das vorherige Programm wurde hart beendet
            catch { _owned = true; }                             // im Zweifel lieber starten als gar nicht
            return _owned;
        }

        /// <summary>Schickt Befehle an das bereits laufende Programm. false = es hat nicht geantwortet.</summary>
        public static bool Send(IEnumerable<string> commands, int timeoutMs)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(timeoutMs);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                foreach (string c in commands) writer.WriteLine(c);
                writer.Flush();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Nimmt ab jetzt Befehle weiterer Programmstarts entgegen. onCommand läuft in einem Hintergrund-Thread.</summary>
        public void Listen(Action<string> onCommand)
        {
            CancellationToken token = _stop.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                     PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                        limit.CancelAfter(3000);                              // ein Absender darf den Kanal nicht blockieren
                        for (int i = 0; i < 20; i++)
                        {
                            string? line = await reader.ReadLineAsync(limit.Token);
                            if (line == null) break;
                            line = line.Trim();
                            if (line.Length > 0 && line.Length <= 200) onCommand(line);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { try { await Task.Delay(500, token); } catch { } }
                }
            });
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            if (_owned)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
                _owned = false;
            }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }
    }
}
