using MyShowsCompanion.Import;
using MyShowsCompanion.Storage;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Использование: MyShowsCompanion <путь-к-Профиль.xlsx>");
    return 2;
}
var baseDir = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
var databasePath = Path.Combine(baseDir, "MyShowsCompanion.sqlite3");
var store = new SqliteStore(databasePath);
store.Initialize();
var result = new WorkbookImporter(store).Import(Path.GetFullPath(args[0]));
Console.WriteLine($"Импортировано: {result.Run.ImportedCount}; обновлено: {result.Run.UpdatedCount}; пропущено: {result.Run.SkippedCount}");
Console.WriteLine($"SQLite: {databasePath}");
if (!string.IsNullOrWhiteSpace(result.Run.ErrorsAndWarnings)) Console.WriteLine(result.Run.ErrorsAndWarnings);
return 0;
