using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Shell;

namespace CompilePalX
{
    internal delegate void OnTitleChange(string title);
    internal delegate void OnProgressChange(double progress);
    static class ProgressManager
    {
        public static event OnTitleChange TitleChange;
        public static event OnProgressChange ProgressChange;

        private static TaskbarItemInfo taskbarInfo;
        private static bool ready;
        private static string defaultTitle = "Compile Pal";

        /// <summary>
        /// The window title, in one place.
        ///
        /// This string was written out six times across two files, which is how five of them kept
        /// upstream's trailing "X" ("Compile Pal 029X") after the sixth was changed. The X is gone
        /// deliberately: this fork numbers itself independently, and a shared decoration on
        /// differently-numbered builds is exactly what makes "which one are you running"
        /// unanswerable.
        /// </summary>
        public static string WindowTitle(string? gameName = null) =>
            $"{defaultTitle} {UpdateManager.CurrentVersion} - " +
            (gameName ?? GameConfigurationManager.GameConfiguration?.Name ?? "no game selected");

        /// <summary>The same title with a percentage in front, for the taskbar during a compile.</summary>
        private static string ProgressTitle(double progress) =>
            $"{Math.Floor(progress * 100d)}% - {WindowTitle()}";

        /// <summary>
        /// Whether there is a taskbar item to talk to yet.
        ///
        /// `ready` alone was not enough, and the difference crashed the application on startup.
        /// Every method below checked it INSIDE the dispatcher lambda, which meant dereferencing
        /// taskbarInfo.Dispatcher to get there - and taskbarInfo is null until Init runs from
        /// MainWindow's constructor.
        ///
        /// So any failure early enough to precede that constructor reached ErrorProgress through
        /// the exception handler, threw a NullReferenceException out of the handler itself, and the
        /// application died without ever showing the dialog that was supposed to explain it. The
        /// only trace was a file in CrashLogs.
        /// </summary>
        private static bool Ready => ready && taskbarInfo is not null;

        static public void Init(TaskbarItemInfo _taskbarInfo)
        {
            taskbarInfo = _taskbarInfo;
            ready = true;

            TitleChange(
	            WindowTitle());
        }


        static public double Progress
        {
            get
            {
                if (!Ready) return 0;

                return taskbarInfo.Dispatcher.Invoke(() => taskbarInfo.ProgressValue);
            }
            set { SetProgress(value); }
        }

        static public void SetProgress(double progress)
        {
            if (Ready)
            {
                taskbarInfo.Dispatcher.Invoke(() =>
                {
                    taskbarInfo.ProgressState = TaskbarItemProgressState.Normal;

                    taskbarInfo.ProgressValue = progress;
                    ProgressChange(progress * 100);

                    if (progress >= 1)
                    {
                        TitleChange(ProgressTitle(progress));

                        if (ConfigurationManager.Settings.PlaySoundOnCompileCompletion)
                        {
                            System.Media.SystemSounds.Exclamation.Play();
                        }
                    }
                    else if (progress <= 0)
                    {
                        taskbarInfo.ProgressState = TaskbarItemProgressState.None;
                        TitleChange(
	                        WindowTitle());
                    }
                    else
                    {
                        TitleChange(ProgressTitle(progress));
                    }
                });

            }
        }

        /// <summary>
        /// Marks the taskbar button as failed.
        ///
        /// Called from the exception handler, which is the reason the guard is at the top rather
        /// than inside the lambda: this has to be safe to call when nothing has been initialised,
        /// because that is exactly when a startup failure calls it.
        /// </summary>
        static public void ErrorProgress()
        {
            if (!Ready) return;

            taskbarInfo.Dispatcher.Invoke(() =>
            {
                // Not SetProgress(1): that treats reaching 1 as a successful finish, playing the
                // completion sound and showing "100%" in the title even though the compile was
                // cancelled or failed, not completed.
                taskbarInfo.ProgressValue = 1;
                ProgressChange(100);
                taskbarInfo.ProgressState = TaskbarItemProgressState.Error;
                TitleChange(WindowTitle());
            });

        }

        static public void PingProgress()
        {
            taskbarInfo.Dispatcher.Invoke(() =>
                                          {
                                              if (ready)
                                              {
                                                  if (taskbarInfo.ProgressValue >= 1)
                                                      SetProgress(0);
                                              }
                                          });
        }
    }
}
