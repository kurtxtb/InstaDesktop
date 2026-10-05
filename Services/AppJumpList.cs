using System;
using System.Windows;
using System.Windows.Shell;
using InstaDesktop.Localization;

namespace InstaDesktop.Services;

// Right-click the taskbar button: open a section, the messages window, or
// pause/resume notifications. Each entry starts InstaDesktop with a fixed
// argument that the running instance receives (see InstanceChannel).
internal static class AppJumpList
{
    public static JumpList Build()
    {
        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        void Add(AppCommand command, string key)
        {
            list.JumpItems.Add(new JumpTask
            {
                Title = Loc.T(key), Description = Loc.T(key), Arguments = AppCommands.Argument(command),
                ApplicationPath = Environment.ProcessPath, IconResourcePath = Environment.ProcessPath, IconResourceIndex = 0
            });
        }
        Add(AppCommand.MessagesWindow, "Jump.MessagesWindow");
        Add(AppCommand.Home, "Jump.Home");
        Add(AppCommand.Messages, "Jump.Messages");
        Add(AppCommand.Reels, "Jump.Reels");
        Add(AppCommand.Explore, "Jump.Explore");
        Add(AppCommand.Pause1h, "Jump.Pause1h");
        Add(AppCommand.Resume, "Jump.Resume");
        return list;
    }

    public static void Apply(Application app)
    {
        try
        {
            var list = Build();
            JumpList.SetJumpList(app, list);
            list.Apply();
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }
}
