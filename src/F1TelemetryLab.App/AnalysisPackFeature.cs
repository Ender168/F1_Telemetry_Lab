using Avalonia.Controls;
using System.Collections;

namespace F1TelemetryLab;

public static class AnalysisPackFeature
{
    public static void Attach(Window window)
    {
        var analyzeButton = EnumerateControls(window)
            .OfType<Button>()
            .FirstOrDefault(button => IsAnalyzeButton(button.Content as string));

        if (analyzeButton?.Parent is not Panel actions)
            return;

        if (actions.Children.OfType<Button>().Any(button => IsPrepareButton(button.Content as string)))
            return;

        var russian = string.Equals(analyzeButton.Content as string, "Проанализировать сессию", StringComparison.Ordinal);
        var prepare = new Button
        {
            Content = russian ? "Подготовить архив для анализа" : "Prepare analysis pack",
            Width = 220
        };

        prepare.Click += async (_, _) => await PrepareSelectedSessionAsync(window, prepare, russian);
        actions.Children.Add(prepare);
    }

    private static async Task PrepareSelectedSessionAsync(Window window, Button button, bool russian)
    {
        var session = EnumerateControls(window)
            .OfType<ListBox>()
            .Select(list => list.SelectedItem)
            .OfType<SessionListItem>()
            .FirstOrDefault();

        if (session is null)
        {
            button.Content = russian ? "Сначала выберите сессию" : "Select a session first";
            return;
        }

        var databasePath = Path.Combine(session.FolderPath, "session.sqlite");
        if (!File.Exists(databasePath))
        {
            button.Content = russian ? "session.sqlite не найден" : "session.sqlite not found";
            return;
        }

        var normalCaption = russian ? "Подготовить архив для анализа" : "Prepare analysis pack";
        button.IsEnabled = false;
        button.Content = russian ? "Подготовка архива..." : "Preparing pack...";

        try
        {
            var zip = await Task.Run(() => SessionPackager.CreateZip(session.FolderPath, databasePath, session.SessionName));
            button.Content = russian ? $"Готово: {Path.GetFileName(zip)}" : $"Ready: {Path.GetFileName(zip)}";
        }
        catch (Exception ex)
        {
            button.Content = russian ? "Ошибка подготовки архива" : "Pack failed";
            Console.Error.WriteLine($"Analysis pack failed: {ex}");
        }
        finally
        {
            button.IsEnabled = true;
        }

        _ = normalCaption;
    }

    private static bool IsAnalyzeButton(string? text) =>
        string.Equals(text, "Analyze selected session", StringComparison.Ordinal) ||
        string.Equals(text, "Проанализировать сессию", StringComparison.Ordinal);

    private static bool IsPrepareButton(string? text) =>
        string.Equals(text, "Prepare analysis pack", StringComparison.Ordinal) ||
        string.Equals(text, "Подготовить архив для анализа", StringComparison.Ordinal);

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                foreach (var nested in EnumerateControls(child))
                    yield return nested;
        }

        if (root is Border border && border.Child is Control borderChild)
        {
            foreach (var nested in EnumerateControls(borderChild))
                yield return nested;
        }

        if (root is ScrollViewer scroll && scroll.Content is Control scrollChild)
        {
            foreach (var nested in EnumerateControls(scrollChild))
                yield return nested;
        }

        if (root is ContentControl content && content.Content is Control contentChild)
        {
            foreach (var nested in EnumerateControls(contentChild))
                yield return nested;
        }

        if (root is ItemsControl items && items.ItemsSource is IEnumerable source)
        {
            foreach (var item in source)
            {
                if (item is not Control itemControl) continue;
                foreach (var nested in EnumerateControls(itemControl))
                    yield return nested;
            }
        }
    }
}
