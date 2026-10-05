using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InstaDesktop.Services;

// Taskbar jump list and command-line actions. A second launch hands its command
// to the running instance over a per-user named pipe and exits; only the fixed
// commands below are accepted, nothing from the pipe is executed or navigated.
public enum AppCommand { None, Home, Messages, Reels, Explore, MessagesWindow, Pause1h, Resume }

public static class AppCommands
{
    private static readonly (string Argument, AppCommand Command)[] Table =
    {
        ("--open=home", AppCommand.Home), ("--open=messages", AppCommand.Messages),
        ("--open=reels", AppCommand.Reels), ("--open=explore", AppCommand.Explore),
        ("--messages-window", AppCommand.MessagesWindow), ("--pause=1h", AppCommand.Pause1h), ("--resume", AppCommand.Resume)
    };

    public static string Argument(AppCommand command) => Table.First(t => t.Command == command).Argument;

    public static AppCommand Parse(string? value) =>
        Table.FirstOrDefault(t => string.Equals(t.Argument, value?.Trim(), StringComparison.OrdinalIgnoreCase)).Command;

    public static AppCommand Find(string[] args) =>
        args.Select(Parse).FirstOrDefault(c => c != AppCommand.None);
}

public sealed class InstanceChannel : IDisposable
{
    private readonly string _name;
    internal string Name => _name;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    public InstanceChannel(string? name = null) =>
        _name = name ?? "InstaDesktop-Commands-" + WindowsIdentity.GetCurrent().User!.Value;

    // Runs on a worker thread; received is called with each valid command.
    public void Listen(Action<AppCommand> received)
    {
        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    // Only this user's processes may connect (CurrentUserOnly).
                    using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    var buffer = new byte[64];
                    int read = await server.ReadAsync(buffer, _stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), _stop.Token);
                    var command = AppCommands.Parse(Encoding.ASCII.GetString(buffer, 0, read));
                    LoggingService.Write(LogEvent.CommandReceived, code: (int)command);
                    if (command != AppCommand.None) received(command);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
                { LoggingService.Write(LogEvent.CommandReceived, error); }
            }
        });
    }

    // From a second launch: true if the running instance took the command.
    public bool Send(AppCommand command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.ASCII.GetBytes(AppCommands.Argument(command));
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            LoggingService.Write(LogEvent.CommandReceived, error);
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(1000); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
