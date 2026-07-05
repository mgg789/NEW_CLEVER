using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
{
    internal static class GitDesignData
    {
        internal static ObservableCollection<GitChangeItemVM> CreateChanges()
        {
            return new ObservableCollection<GitChangeItemVM>
            {
                CreateFile("Main.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF"),
                CreateFile("RobotDrive.bpi", "Includes", "A", "added", "BPI", "#3F9D64", "#23B469"),
                CreateFile("lesson-notes.txt", "Docs", "M", "modified", "TXT", "#9B8B65", "#8F98AB"),
                CreateFile("old-backup.bp", "Archive", "D", "deleted", "BP", "#D45B55", "#FF4133")
            };
        }

        internal static IList<GitCommitVM> CreateCommits()
        {
            var commits = new List<GitCommitVM>
            {
                CreateCommit("a9c4e2d", "Резервная копия перед уроком", "Maksim", "2 min ago", "4 files changed, 38 insertions(+), 6 deletions(-)", new[] { "c8d21aa", "f31b0d2" }, new[] { "develop" }, new[] { "origin/develop" },
                    CreateFile("Main.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF"),
                    CreateFile("RobotDrive.bpi", "Includes", "A", "added", "BPI", "#3F9D64", "#23B469")),
                CreateCommit("c8d21aa", "Добавлен экран Git в сайдбар", "Maksim", "18 min ago", "6 files changed, 210 insertions(+)", new[] { "c1b55e4" }, new string[0], new string[0],
                    CreateFile("GitPanel.xaml", "View\\Panels", "A", "added", "XAML", "#3F9D64", "#46A6D9")),
                CreateCommit("f31b0d2", "Эксперимент с подсказками для коммитов", "Anna", "22 min ago", "3 files changed, 47 insertions(+), 3 deletions(-)", new[] { "f09ca91" }, new[] { "feature/git-help" }, new string[0],
                    CreateFile("GitPanelVM.cs", "ViewModel\\PanelsVM", "M", "modified", "CS", "#9B8B65", "#46A6D9")),
                CreateCommit("f09ca91", "Набросок обучающих подсказок", "Anna", "35 min ago", "2 files changed, 24 insertions(+)", new[] { "e62b7ac" }, new string[0], new string[0],
                    CreateFile("interface_ru.txt", "Localization\\ru", "M", "modified", "TXT", "#9B8B65", "#8F98AB")),
                CreateCommit("c1b55e4", "Подключены иконки правой панели", "Maksim", "41 min ago", "4 files changed, 96 insertions(+), 5 deletions(-)", new[] { "e62b7ac" }, new string[0], new string[0],
                    CreateFile("GitIcons.xaml", "Dictionary", "A", "added", "XAML", "#3F9D64", "#46A6D9")),
                CreateCommit("e62b7ac", "Сохранена рабочая версия проекта", "Maksim", "1 hour ago", "9 files changed, 120 insertions(+), 28 deletions(-)", new[] { "b7d42c5", "r2ad401" }, new string[0], new string[0],
                    CreateFile("Drive.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF")),
                CreateCommit("r2ad401", "Проверены звуки и изображения EV3", "Dima", "1 hour ago", "5 files changed, 18 insertions(+)", new[] { "r1c8300" }, new[] { "backup/media" }, new string[0],
                    CreateFile("Smile.png", "Resources\\Media", "A", "added", "PNG", "#3F9D64", "#41B883")),
                CreateCommit("r1c8300", "Добавлены медиа для демонстрации", "Dima", "2 hours ago", "8 files changed, 8 insertions(+)", new[] { "b2ac335" }, new string[0], new string[0],
                    CreateFile("Start.wav", "Resources\\Media", "A", "added", "WAV", "#3F9D64", "#41B883")),
                CreateCommit("b7d42c5", "Исправлена команда поворота робота", "Maksim", "2 hours ago", "2 files changed, 12 insertions(+), 4 deletions(-)", new[] { "b2ac335" }, new string[0], new string[0],
                    CreateFile("RobotDrive.bpi", "Includes", "M", "modified", "BPI", "#9B8B65", "#0A86FF")),
                CreateCommit("b2ac335", "Начальный проект урока", "Maksim", "yesterday", "3 files changed, 70 insertions(+)", new[] { "700ad12" }, new string[0], new string[0],
                    CreateFile("Main.bp", "Programs", "A", "added", "BP", "#3F9D64", "#0A86FF")),
                CreateCommit("700ad12", "Создан проект робота", "Maksim", "yesterday", "1 file changed, 12 insertions(+)", new string[0], new[] { "main" }, new string[0],
                    CreateFile("project.bp", "Programs", "A", "added", "BP", "#3F9D64", "#0A86FF"))
            };

            return commits;
        }

        private static GitCommitVM CreateCommit(string hash, string message, string author, string timeText, string stats, string[] parents, string[] localRefs, string[] remoteRefs, params GitChangeItemVM[] files)
        {
            var commit = new GitCommitVM
            {
                Hash = hash,
                ShortHash = hash.Length > 7 ? hash.Substring(0, 7) : hash,
                Message = message,
                Author = author,
                TimeText = timeText,
                Summary = message,
                StatsText = stats
            };

            foreach (var parent in parents)
                commit.ParentHashes.Add(parent);
            foreach (var localRef in localRefs)
                commit.LocalRefs.Add(localRef);
            foreach (var remoteRef in remoteRefs)
                commit.RemoteRefs.Add(remoteRef);
            foreach (var file in files)
                commit.Files.Add(file);
            commit.DiffDocument = CreateDiffDocument(commit, files);

            return commit;
        }

        private static GitDiffDocumentVM CreateDiffDocument(GitCommitVM commit, GitChangeItemVM[] files)
        {
            var document = new GitDiffDocumentVM(commit.ShortHash, "Diff " + commit.ShortHash + ".diff");

            foreach (var file in files)
            {
                var filePath = string.IsNullOrEmpty(file.Folder) ? file.FileName : file.Folder + "\\" + file.FileName;
                var diffFile = new GitDiffFileVM(filePath);
                AddFileDiff(diffFile, file, commit);
                document.Files.Add(diffFile);
            }

            return document;
        }

        private static void AddFileDiff(GitDiffFileVM diffFile, GitChangeItemVM file, GitCommitVM commit)
        {
            if (file.Status == "A")
            {
                diffFile.Lines.Add(new GitDiffLineVM("+// " + commit.Message, GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM("+start", GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM("+  display = \"" + file.FileName + "\"", GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM("+  wait 1000", GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM("+end", GitDiffLineKind.Added));
            }
            else if (file.Status == "D")
            {
                diffFile.Lines.Add(new GitDiffLineVM("-// Старый файл проекта", GitDiffLineKind.Deleted));
                diffFile.Lines.Add(new GitDiffLineVM("-start", GitDiffLineKind.Deleted));
                diffFile.Lines.Add(new GitDiffLineVM("-  display = \"backup\"", GitDiffLineKind.Deleted));
                diffFile.Lines.Add(new GitDiffLineVM("-end", GitDiffLineKind.Deleted));
            }
            else
            {
                diffFile.Lines.Add(new GitDiffLineVM(" start", GitDiffLineKind.Context));
                diffFile.Lines.Add(new GitDiffLineVM("-  speed = 20", GitDiffLineKind.Deleted));
                diffFile.Lines.Add(new GitDiffLineVM("+  speed = 35", GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM("   steering = 0", GitDiffLineKind.Context));
                diffFile.Lines.Add(new GitDiffLineVM("-  wait 500", GitDiffLineKind.Deleted));
                diffFile.Lines.Add(new GitDiffLineVM("+  wait 700", GitDiffLineKind.Added));
                diffFile.Lines.Add(new GitDiffLineVM(" end", GitDiffLineKind.Context));
            }

            diffFile.Lines.Add(new GitDiffLineVM("", GitDiffLineKind.Context));
        }

        private static GitChangeItemVM CreateFile(string name, string folder, string status, string statusText, string type, string statusColor, string iconColor)
        {
            var item = new GitChangeItemVM
            {
                FileName = name,
                Folder = folder,
                Status = status,
                StatusText = statusText,
                FileType = type,
                StatusBrush = CreateBrush(statusColor),
                IconBrush = CreateBrush(iconColor)
            };

            item.ApplyFixedFileType();
            return item;
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
