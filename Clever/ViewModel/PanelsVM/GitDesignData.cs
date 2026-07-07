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
                CreateCommit("m12f9a1", "Резервная копия перед уроком", "Maksim", "2 min ago", "4 files changed, 38 insertions(+), 6 deletions(-)", new[] { "m11d831", "f3b7710" }, new[] { "develop" }, new[] { "origin/develop" },
                    CreateFile("Main.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF"),
                    CreateFile("RobotDrive.bpi", "Includes", "A", "added", "BPI", "#3F9D64", "#23B469")),
                CreateCommit("u3e521b", "Проверка датчиков на полигоне", "Dima", "6 min ago", "2 files changed, 19 insertions(+)", new[] { "u2ac701" }, new[] { "experiment/sensors" }, new string[0],
                    CreateFile("Sensors.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF")),
                CreateCommit("m11d831", "Слияние длинной ветки настроек", "Maksim", "10 min ago", "7 files changed, 112 insertions(+)", new[] { "m10ac43", "l4d91b0" }, new string[0], new string[0],
                    CreateFile("Settings.bpi", "Includes", "M", "modified", "BPI", "#9B8B65", "#23B469")),
                CreateCommit("l4d91b0", "Финальная проверка настроек", "Anna", "13 min ago", "3 files changed, 21 insertions(+)", new[] { "l3c48a2" }, new[] { "feature/long-settings" }, new string[0],
                    CreateFile("settings.csv", "Docs", "M", "modified", "CSV", "#9B8B65", "#FF9F1A")),
                CreateCommit("f3b7710", "Эксперимент с подсказками для коммитов", "Anna", "22 min ago", "3 files changed, 47 insertions(+), 3 deletions(-)", new[] { "f2aa912", "n2f60c5" }, new[] { "feature/git-help" }, new string[0],
                    CreateFile("GitPanelVM.cs", "ViewModel\\PanelsVM", "M", "modified", "CS", "#9B8B65", "#46A6D9")),
                CreateCommit("n2f60c5", "Вложенная ветка с текстами подсказок", "Anna", "28 min ago", "2 files changed, 16 insertions(+)", new[] { "n1c3110" }, new[] { "feature/git-help/texts" }, new string[0],
                    CreateFile("interface_ru.txt", "Localization\\ru", "M", "modified", "TXT", "#9B8B65", "#8F98AB")),
                CreateCommit("f2aa912", "Черновик обучающих подсказок", "Anna", "35 min ago", "2 files changed, 24 insertions(+)", new[] { "f1d84e2" }, new string[0], new string[0],
                    CreateFile("help.md", "Docs", "A", "added", "MD", "#3F9D64", "#8F98AB")),
                CreateCommit("m10ac43", "Добавлен экран Git в сайдбар", "Maksim", "41 min ago", "6 files changed, 210 insertions(+)", new[] { "m09f67d" }, new string[0], new string[0],
                    CreateFile("GitPanel.xaml", "View\\Panels", "A", "added", "XAML", "#3F9D64", "#46A6D9")),
                CreateCommit("u2ac701", "Логирование показаний датчиков", "Dima", "45 min ago", "1 file changed, 14 insertions(+)", new[] { "u1b8074" }, new string[0], new string[0],
                    CreateFile("sensor-log.txt", "Docs", "A", "added", "TXT", "#3F9D64", "#8F98AB")),
                CreateCommit("l3c48a2", "Настроены параметры движения", "Anna", "49 min ago", "4 files changed, 33 insertions(+)", new[] { "l2ad375" }, new string[0], new string[0],
                    CreateFile("Drive.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF")),
                CreateCommit("n1c3110", "Добавлены короткие тексты ошибок", "Anna", "54 min ago", "2 files changed, 11 insertions(+)", new[] { "f1d84e2" }, new string[0], new string[0],
                    CreateFile("errors.txt", "Docs", "A", "added", "TXT", "#3F9D64", "#8F98AB")),
                CreateCommit("m09f67d", "Подключены иконки правой панели", "Maksim", "1 hour ago", "4 files changed, 96 insertions(+), 5 deletions(-)", new[] { "m08ab04", "a2c42de" }, new string[0], new string[0],
                    CreateFile("GitIcons.xaml", "Dictionary", "A", "added", "XAML", "#3F9D64", "#46A6D9")),
                CreateCommit("a2c42de", "Первая параллельная ветка готова", "Maksim", "1 hour ago", "2 files changed, 18 insertions(+)", new[] { "a1e2020" }, new[] { "feature/panel-icons" }, new string[0],
                    CreateFile("branch.svg", "Resources\\Icons", "A", "added", "SVG", "#3F9D64", "#46A6D9")),
                CreateCommit("f1d84e2", "Базовая структура подсказок", "Anna", "1 hour ago", "2 files changed, 9 insertions(+)", new[] { "m08ab04" }, new string[0], new string[0],
                    CreateFile("git-help.txt", "Docs", "A", "added", "TXT", "#3F9D64", "#8F98AB")),
                CreateCommit("u1b8074", "Начат эксперимент с датчиками", "Dima", "1 hour ago", "1 file changed, 7 insertions(+)", new[] { "m08ab04" }, new string[0], new string[0],
                    CreateFile("Sensors.bp", "Programs", "A", "added", "BP", "#3F9D64", "#0A86FF")),
                CreateCommit("l2ad375", "Промежуточная настройка скоростей", "Anna", "1 hour ago", "3 files changed, 22 insertions(+)", new[] { "l1ab220" }, new string[0], new string[0],
                    CreateFile("speed.csv", "Docs", "M", "modified", "CSV", "#9B8B65", "#FF9F1A")),
                CreateCommit("m08ab04", "Сохранена рабочая версия проекта", "Maksim", "2 hours ago", "9 files changed, 120 insertions(+), 28 deletions(-)", new[] { "m07cd10" }, new string[0], new string[0],
                    CreateFile("Drive.bp", "Programs", "M", "modified", "BP", "#9B8B65", "#0A86FF")),
                CreateCommit("a1e2020", "Набор иконок для панели", "Maksim", "2 hours ago", "3 files changed, 31 insertions(+)", new[] { "m06db77" }, new string[0], new string[0],
                    CreateFile("panel-right.svg", "Resources\\Icons", "A", "added", "SVG", "#3F9D64", "#46A6D9")),
                CreateCommit("l1ab220", "Старт длинной ветки настроек", "Anna", "2 hours ago", "2 files changed, 12 insertions(+)", new[] { "m04aa98" }, new string[0], new string[0],
                    CreateFile("Settings.bpi", "Includes", "A", "added", "BPI", "#3F9D64", "#23B469")),
                CreateCommit("m07cd10", "Слияние ветки с медиа", "Maksim", "2 hours ago", "5 files changed, 18 insertions(+)", new[] { "m06db77", "b2bb410" }, new string[0], new string[0],
                    CreateFile("Smile.png", "Resources\\Media", "A", "added", "PNG", "#3F9D64", "#41B883")),
                CreateCommit("b2bb410", "Проверены звуки и изображения EV3", "Dima", "2 hours ago", "5 files changed, 18 insertions(+)", new[] { "b1a9100" }, new[] { "backup/media" }, new string[0],
                    CreateFile("Smile.png", "Resources\\Media", "A", "added", "PNG", "#3F9D64", "#41B883")),
                CreateCommit("b1a9100", "Добавлены медиа для демонстрации", "Dima", "3 hours ago", "8 files changed, 8 insertions(+)", new[] { "m06db77" }, new string[0], new string[0],
                    CreateFile("Start.wav", "Resources\\Media", "A", "added", "WAV", "#3F9D64", "#41B883")),
                CreateCommit("m06db77", "Исправлена команда поворота робота", "Maksim", "3 hours ago", "2 files changed, 12 insertions(+), 4 deletions(-)", new[] { "m05fa19" }, new string[0], new string[0],
                    CreateFile("RobotDrive.bpi", "Includes", "M", "modified", "BPI", "#9B8B65", "#0A86FF")),
                CreateCommit("m05fa19", "Добавлен стартовый маршрут", "Maksim", "yesterday", "3 files changed, 33 insertions(+)", new[] { "m04aa98" }, new string[0], new string[0],
                    CreateFile("Route.bpm", "Programs", "A", "added", "BPM", "#3F9D64", "#0A86FF")),
                CreateCommit("m04aa98", "Начальный проект урока", "Maksim", "yesterday", "3 files changed, 70 insertions(+)", new[] { "m03cc33" }, new string[0], new string[0],
                    CreateFile("Main.bp", "Programs", "A", "added", "BP", "#3F9D64", "#0A86FF")),
                CreateCommit("m03cc33", "Подготовлены папки проекта", "Maksim", "yesterday", "2 files changed, 8 insertions(+)", new[] { "m02ba20" }, new string[0], new string[0],
                    CreateFile("lesson-notes.txt", "Docs", "A", "added", "TXT", "#3F9D64", "#8F98AB")),
                CreateCommit("m02ba20", "Выбран шаблон робота", "Maksim", "2 days ago", "1 file changed, 10 insertions(+)", new[] { "m01aa10" }, new string[0], new string[0],
                    CreateFile("template.bp", "Programs", "A", "added", "BP", "#3F9D64", "#0A86FF")),
                CreateCommit("m01aa10", "Создан проект робота", "Maksim", "2 days ago", "1 file changed, 12 insertions(+)", new string[0], new[] { "main" }, new string[0],
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
