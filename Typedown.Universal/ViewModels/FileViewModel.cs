﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Typedown.Universal.Controls;
using Typedown.Universal.Interfaces;
using Typedown.Universal.Services;
using Typedown.Universal.Utilities;
using Windows.ApplicationModel.Resources;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Typedown.Universal.ViewModels
{
    public sealed partial class FileViewModel : INotifyPropertyChanged, IDisposable
    {
        public IServiceProvider ServiceProvider { get; }

        public AppViewModel AppViewModel => ServiceProvider.GetService<AppViewModel>();

        public SettingsViewModel SettingsViewModel => ServiceProvider.GetService<SettingsViewModel>();

        public EditorViewModel EditorViewModel => ServiceProvider.GetService<EditorViewModel>();

        public EventCenter EventCenter => ServiceProvider.GetService<EventCenter>();

        public RemoteInvoke RemoteInvoke => ServiceProvider.GetService<RemoteInvoke>();

        public AccessHistory AccessHistory => ServiceProvider.GetService<AccessHistory>();

        public string WorkFolder { get; private set; } = null;

        public string FilePath { get; private set; } = null;

        public string ImageBasePath => string.IsNullOrEmpty(FilePath) ? SettingsViewModel.DefaultImageBasePath : Path.GetDirectoryName(FilePath);

        public string FileName => Path.GetFileName(FilePath);

        public Command<Unit> NewFileCommand { get; } = new();
        public Command<string> NewWindowCommand { get; } = new();
        public Command<string> OpenFileCommand { get; } = new();
        public Command<string> OpenFolderCommand { get; } = new();
        public Command<Unit> NewFolderCommand { get; } = new();
        public Command<Unit> ClearHistoryCommand { get; } = new();
        public Command<Unit> SaveCommand { get; } = new();
        public Command<Unit> SaveAsCommand { get; } = new();
        public Command<Unit> ImportCommand { get; } = new();
        public Command<string> ExportCommand { get; } = new();
        public Command<Unit> PrintCommand { get; } = new();
        public Command<Unit> ExitCommand { get; } = new();

        private readonly ResourceLoader dialogMessages = ResourceLoader.GetForViewIndependentUse("DialogMessages");

        private readonly DispatcherTimer saveFileTimer = new();

        public AutoBackup AutoBackup => ServiceProvider.GetService<AutoBackup>();

        public IMarkdownEditor MarkdownEditor => ServiceProvider.GetService<IMarkdownEditor>();

        private readonly CompositeDisposable disposables = new();

        public FileViewModel(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
            NewFileCommand.OnExecute.Subscribe(async _ => await NewFileFun());
            OpenFileCommand.OnExecute.Subscribe(OpenFile);
            OpenFolderCommand.OnExecute.Subscribe(OpenFolder);
            SaveAsCommand.OnExecute.Subscribe(async _ => await SaveAs());
            SaveCommand.OnExecute.Subscribe(async _ => await Save());
            ExitCommand.OnExecute.Subscribe(_ => Exit());
            ClearHistoryCommand.OnExecute.Subscribe(x => { _ = AccessHistory.ClearHistory(); });
            ExportCommand.OnExecute.Subscribe(Export);
            PrintCommand.OnExecute.Subscribe(_ => Print());
            ImportCommand.OnExecute.Subscribe(_ => Import());
            RemoteInvoke.Handle<JToken, Task<bool>>("WriteAllText", WriteAllText);
            RemoteInvoke.Handle<JToken, Task<bool>>("ConvertHTML", ConvertHTML);
            RemoteInvoke.Handle<JToken, bool>("PrintHTML", PrintHTML);
            saveFileTimer.Interval = TimeSpan.FromSeconds(5);
            saveFileTimer.Tick += SaveFileTimerTick;
            saveFileTimer.Start();
            _ = App.Dispatcher.RunIdleAsync(_ => OnStartup());
        }

        private async void SaveFileTimerTick(object sender, object e)
        {
            if (SettingsViewModel.AutoSave)
            {
                EditorViewModel.AutoSavedSucc = await AutoSaveFile();
                if (!EditorViewModel.AutoSavedSucc)
                    await AutoBackupFile();
            }
            else
            {
                await AutoBackupFile();
            }
        }

        public async Task<bool> AutoSaveFile()
        {
            if (SettingsViewModel.AutoSave && (EditorViewModel.FileHash != EditorViewModel.CurrentHash) && FilePath != null)
                return await Save(false);
            return FilePath != null;
        }

        private async Task<bool> AutoBackupFile()
        {
            if ((EditorViewModel.FileHash != EditorViewModel.CurrentHash))
                return await AutoBackup.Backup(FilePath, EditorViewModel.Markdown);
            return true;
        }

        private async Task NewFileFun(bool postMessage = true)
        {
            if (!await AskToSave()) return;
            FilePath = null;
            EditorViewModel.FileHash = Common.SimpleHash(Common.DefaultMarkdwn);
            var backup = await CheckBackup(FilePath, EditorViewModel.FileHash);
            if (backup == null)
            {
                EditorViewModel.Markdown = Common.DefaultMarkdwn;
                EditorViewModel.CurrentHash = EditorViewModel.FileHash;
                EditorViewModel.Saved = true;
                EditorViewModel.AutoSavedSucc = true;
                EditorViewModel.FileLoaded = false;
            }
            else
            {
                EditorViewModel.Markdown = backup;
                EditorViewModel.CurrentHash = Common.SimpleHash(backup);
                EditorViewModel.Saved = false;
                EditorViewModel.AutoSavedSucc = false;
                EditorViewModel.FileLoaded = true;
            }
            EditorViewModel.History.InitHistory(Common.DefaultMarkdwn);
            if (postMessage)
            {
                MarkdownEditor?.PostMessage("LoadFile", EditorViewModel.Markdown);
            }
        }

        private async void OpenFile(string filePath = null)
        {
            if (!await AskToSave()) return;
            filePath ??= await AppViewModel.MainWindow.PickMarkdownFolderAsync();
            if (filePath != null)
                await LoadFile(filePath, true);
        }

        private async void OpenFolder(string folderPath = null)
        {
            folderPath ??= await AppViewModel.MainWindow.PickMarkdownFolderAsync();
            if (folderPath != null)
            {
                if (await LoadFolder(folderPath))
                {
                    SettingsViewModel.SidePaneOpen = true;
                    SettingsViewModel.SidePaneIndex = 0;
                }
            }
        }

        private async Task<bool> LoadFile(string path, bool skipSavedCheck = false, bool postMessage = true)
        {
            try
            {
                if (TryGetOpenedWindow(path, out var window) && window != AppViewModel.MainWindow)
                {
                    PInvoke.SetForegroundWindow(window);
                    return false;
                }
                if (!File.Exists(path))
                {
                    _ = AccessHistory.RemoveFileHistory(path);
                    throw new FileNotFoundException("File does not exist.");
                }
                else if (!skipSavedCheck && !await AskToSave())
                {
                    return false;
                }
                var text = await File.ReadAllTextAsync(path);
                EditorViewModel.FirstStart = false;
                EditorViewModel.FileHash = Common.SimpleHash(text);
                FilePath = path;
                _ = AccessHistory.RecordFileHistory(FilePath);
                var backup = await CheckBackup(path, EditorViewModel.CurrentHash);
                if (backup == null)
                {
                    EditorViewModel.Markdown = text;
                    EditorViewModel.CurrentHash = EditorViewModel.FileHash;
                    EditorViewModel.Saved = true;
                    EditorViewModel.FileLoaded = false;
                }
                else
                {
                    EditorViewModel.Markdown = backup;
                    EditorViewModel.CurrentHash = Common.SimpleHash(backup);
                    EditorViewModel.Saved = false;
                    EditorViewModel.FileLoaded = true;
                }
                EditorViewModel.AutoSavedSucc = true;
                EditorViewModel.History.InitHistory(EditorViewModel.Markdown);
                if (postMessage)
                {

                    MarkdownEditor?.PostMessage("LoadFile", EditorViewModel.Markdown);
                }
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(dialogMessages.GetString("ReadErrorTitle"), ex.Message, dialogMessages.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private async Task<bool> LoadFolder(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    _ = AccessHistory.RemoveFolderHistory(folderPath);
                    throw new FileNotFoundException("Folder does not exist.");
                }
                WorkFolder = folderPath;
                _ = AccessHistory.RecordFolderHistory(folderPath);
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create("Error", ex.Message, dialogMessages.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private async Task<string> CheckBackup(string path, ulong fileHash)
        {
            string text = await AutoBackup.GetBackup(path);
            if (text == null || Common.SimpleHash(text) == fileHash) return null;
            var dialog = AppContentDialog.Create();
            dialog.Title = dialogMessages.GetString("RecoverTitle");
            dialog.Content = dialogMessages.GetString("RecoverContent");
            dialog.PrimaryButtonText = dialogMessages.GetString("Recover");
            dialog.SecondaryButtonText = dialogMessages.GetString("Delete");
            dialog.DefaultButton = ContentDialogButton.Primary;
            var result = await dialog.ShowAsync(AppViewModel.XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                return text;
            }
            else
            {
                AutoBackup.DeleteBackup(path);
                return null;
            }
        }

        private async Task<bool> Save(bool alert = true)
        {
            if (FilePath == null)
            {
                var result = await SaveAs();
                return result != null;
            }
            else
            {
                var result = (bool)await WriteAllText(JObject.FromObject(new { path = FilePath, text = EditorViewModel.Markdown, alert }));
                if (result)
                {
                    EditorViewModel.FileHash = EditorViewModel.CurrentHash;
                    EditorViewModel.Saved = true;
                    _ = AccessHistory.RecordFileHistory(FilePath);
                }
                return result;
            }
        }

        private async Task<string> SaveAs()
        {
            var filePicker = new FileSavePicker();
            filePicker.SetOwnerWindow(AppViewModel.MainWindow);
            filePicker.FileTypeChoices.Add("Markdown Files", FileTypeHelper.Markdown.ToList());
            filePicker.SuggestedFileName = FileName ?? "untitled";
            var file = await filePicker.PickSaveFileAsync();
            if (file != null)
            {
                var result = (bool)await WriteAllText(JObject.FromObject(new { path = file.Path, text = EditorViewModel.Markdown }));
                if (result)
                {
                    if (FilePath == null)
                    {
                        FilePath = file.Path;
                        AutoBackup.DeleteBackup(null);
                    }
                    EditorViewModel.FileHash = EditorViewModel.CurrentHash;
                    EditorViewModel.Saved = true;
                    _ = AccessHistory.RecordFileHistory(FilePath);
                    return file.Path;
                }
            }
            return null;
        }

        private async Task<bool> ConvertHTML(JToken arg)
        {
            var html = arg["html"].ToString();
            var format = arg["format"].ToString();
            var path = arg["path"].ToString();
            try
            {
                if (format == "pdf")
                {
                    var fileExport = ServiceProvider.GetService<IFileExport>();
                    fileExport.HtmlToPdf(Path.GetDirectoryName(FilePath), html, FilePath, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(dialogMessages.GetString("ExportErrorTitle"), ex.Message, dialogMessages.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private bool PrintHTML(JToken arg)
        {
            var html = arg["html"].ToString();
            var fileExport = ServiceProvider.GetService<IFileExport>();
            fileExport.Print(Path.GetDirectoryName(FilePath), html);
            return true;
        }

        private async Task<bool> WriteAllText(JToken arg)
        {
            try
            {
                await File.WriteAllTextAsync(arg["path"].ToString(), arg["text"].ToString());
                return true;
            }
            catch (Exception ex)
            {
                if (arg["alert"] == null || arg["alert"].ToObject<bool>())
                {
                    await AppContentDialog.Create(dialogMessages.GetString("SaveErrorTitle"), ex.Message, dialogMessages.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                }
                return false;
            }
        }

        private bool askToSaveOpened;

        public async Task<bool> AskToSave()
        {
            if (EditorViewModel.Saved || (SettingsViewModel.AutoSave && await AutoSaveFile()))
            {
                return true;
            }
            if (askToSaveOpened)
            {
                return false;
            }
            askToSaveOpened = true;
            var result = await AppContentDialog.Create(
                dialogMessages.GetString("AsKToSaveTitle"),
                dialogMessages.GetString("AsKToSaveContent"),
                dialogMessages.GetString("Cancel"),
                dialogMessages.GetString("Save"),
                dialogMessages.GetString("Don'tSave")).ShowAsync(AppViewModel.XamlRoot);
            askToSaveOpened = false;
            switch (result)
            {
                case ContentDialogResult.Primary:
                    var saveResult = await Save();
                    return saveResult;
                case ContentDialogResult.Secondary:
                    AutoBackup.DeleteBackup(FilePath);
                    return true;
                case ContentDialogResult.None:
                    return false;
            }
            return false;
        }

        public async Task LoadStartUpMarkdown()
        {
            var path = CommandLine.GetOpenFilePath(AppViewModel.CommandLineArgs);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    await LoadFile(path, true, false);
                }
                catch (Exception)
                {
                    await NewFileFun(false);
                }
            }
            else
            {
                await NewFileFun(false);
            }
        }

        private async void Export(string type)
        {
            var filePicker = new FileSavePicker();
            filePicker.SetOwnerWindow(AppViewModel.MainWindow);
            filePicker.FileTypeChoices.Add(type, new List<string> { "." + type });
            var file = await filePicker.PickSaveFileAsync();
            if (file == null) return;
            MarkdownEditor?.PostMessage("Export", new { type, path = file.Path, title = file.DisplayName });
        }

        private void Print()
        {
            MarkdownEditor?.PostMessage("Export", new { type = "print", title = FileName ?? "untitled" });
        }

        private async void Import()
        {
            try
            {
                var filePicker = new FileOpenPicker();
                filePicker.FileTypeFilter.Add(".html");
                var file = await filePicker.PickSingleFileAsync();
                if (file != null)
                {
                    var text = await File.ReadAllTextAsync(file.Path);
                    MarkdownEditor?.PostMessage("ImportFile", new { type = Path.GetExtension(file.Path).Substring(1), text });
                }
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(dialogMessages.GetString("ImportErrorTitle"), ex.Message, dialogMessages.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
            }
        }

        private async void OnStartup()
        {
            if (string.IsNullOrEmpty(WorkFolder))
            {
                switch (SettingsViewModel.FolderStartupAction)
                {
                    case Enums.FolderStartupAction.OpenLast:
                        await AccessHistory.EnsureInitialized();
                        if (AccessHistory.FolderRecentlyOpened.FirstOrDefault() is string lastFolder && Directory.Exists(lastFolder))
                            await LoadFolder(lastFolder);
                        break;
                    case Enums.FolderStartupAction.OpenFolder:
                        if (Directory.Exists(SettingsViewModel.StartupOpenFolder))
                            await LoadFolder(SettingsViewModel.StartupOpenFolder);
                        break;
                }
            }
            if (string.IsNullOrEmpty(FilePath))
            {
                switch (SettingsViewModel.FileStartupAction)
                {
                    case Enums.FileStartupAction.OpenLast:
                        await AccessHistory.EnsureInitialized();
                        if (AccessHistory.FileRecentlyOpened.FirstOrDefault() is string lastFile && !TryGetOpenedWindow(lastFile, out _) && File.Exists(lastFile))
                            await LoadFile(lastFile, true);
                        break;
                }
            }
        }

        public static bool TryGetOpenedWindow(string filePath, out IntPtr window)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                window = default;
                return false;
            }
            window = AppViewModel.GetInstances().Where(x => x.FileViewModel.FilePath?.ToLower() == filePath.ToLower()).FirstOrDefault()?.MainWindow ?? default;
            return window != default;
        }

        public void Dispose()
        {
            saveFileTimer.Stop();
            disposables.Dispose();
        }

        private void Exit()
        {
            var SC_CLOSE = 0xF060;
            PInvoke.PostMessage(AppViewModel.MainWindow, (uint)PInvoke.WindowMessage.WM_SYSCOMMAND, (nint)SC_CLOSE, IntPtr.Zero);
        }
    }
}
