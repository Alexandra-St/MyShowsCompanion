using System.Globalization;
using ClosedXML.Excel;
using MyShowsCompanion.Domain;

namespace MyShowsCompanion.Import;

public sealed class MyShowsWorkbookReader
{
    private static readonly string[] RequiredSheets = ["Сериалы", "Эпизоды", "Фильмы"];

    public ParsedWorkbook Read(string path)
    {
        using var workbook = new XLWorkbook(path);
        foreach (var name in RequiredSheets)
            if (!workbook.Worksheets.TryGetWorksheet(name, out _))
                throw new InvalidDataException($"Не найден лист «{name}».");

        var warnings = new List<string>();
        var series = ReadRows(workbook.Worksheet("Сериалы"), ["Название", "Статус", "Оценка",
            "Длительность серии, мин.", "Просмотрено эпизодов", "Осталось эпизодов", "Потрачено часов", "Осталось часов"])
            .Select(r => new Series(Guid.NewGuid(), Text(r, "Название"), Text(r, "Статус"), Decimal(r, "Оценка"),
                Int(r, "Длительность серии, мин."), Int(r, "Просмотрено эпизодов"), Int(r, "Осталось эпизодов"),
                Decimal(r, "Потрачено часов"), Decimal(r, "Осталось часов")))
            .Where(x => Keep(x.Title, "сериал", warnings)).ToArray();

        var episodes = ReadRows(workbook.Worksheet("Эпизоды"), ["Сериал", "Сезон", "Эпизод", "Название", "Дата просмотра", "Оценка"])
            .Select(r => new WatchedEpisode(Guid.NewGuid(), null, Text(r, "Сериал"), Int(r, "Сезон"), Int(r, "Эпизод"),
                EmptyToNull(Text(r, "Название")), Date(r, "Дата просмотра")))
            .Where(x => Keep(x.SeriesTitleFromSource, "эпизод без названия сериала", warnings)).ToArray();

        var movies = ReadRows(workbook.Worksheet("Фильмы"), ["Название", "Статус", "Длительность фильма, мин.", "Дата просмотра", "Оценка"])
            .Select(r => new Movie(Guid.NewGuid(), Text(r, "Название"), Text(r, "Статус"), Decimal(r, "Оценка"),
                Int(r, "Длительность фильма, мин."), Date(r, "Дата просмотра")))
            .Where(x => Keep(x.Title, "фильм", warnings)).ToArray();

        return new ParsedWorkbook(series, movies, episodes, warnings);
    }

    private static IEnumerable<Dictionary<string, IXLCell>> ReadRows(IXLWorksheet sheet, string[] required)
    {
        var header = sheet.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.Ordinal);
        foreach (var name in required)
            if (!header.ContainsKey(name)) throw new InvalidDataException($"На листе «{sheet.Name}» не найдена колонка «{name}».");
        foreach (var row in sheet.RowsUsed().Skip(1))
            yield return header.ToDictionary(k => k.Key, k => row.Cell(k.Value), StringComparer.Ordinal);
    }
    private static string Text(IReadOnlyDictionary<string, IXLCell> r, string key) => r[key].GetString().Trim();
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool Keep(string title, string kind, ICollection<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(title)) return true;
        warnings.Add($"Пропущена строка: отсутствует название ({kind})."); return false;
    }
    private static decimal? Decimal(IReadOnlyDictionary<string, IXLCell> r, string key)
    {
        var c = r[key]; if (c.IsEmpty()) return null;
        if (c.TryGetValue<decimal>(out var n)) return n;
        var s = c.GetString().Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out n) ||
               decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("ru-RU"), out n) ? n : null;
    }
    private static int? Int(IReadOnlyDictionary<string, IXLCell> r, string key)
    {
        var c = r[key]; if (c.IsEmpty()) return null;
        if (c.TryGetValue<int>(out var n)) return n;
        var s = c.GetString().Trim();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : null;
    }
    private static DateOnly? Date(IReadOnlyDictionary<string, IXLCell> r, string key)
    {
        var c = r[key]; if (c.IsEmpty()) return null;
        if (c.TryGetValue<DateTime>(out var dt)) return DateOnly.FromDateTime(dt);
        var s = c.GetString().Trim();
        if (DateOnly.TryParse(s, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var d)) return d;
        if (DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }
}
