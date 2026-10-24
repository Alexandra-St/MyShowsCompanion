using ClosedXML.Excel;
using MyShowsCompanion.Import;
using MyShowsCompanion.Services;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Tests;

public sealed class WorkbookImportTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    public WorkbookImportTests()=>Directory.CreateDirectory(_dir);
    public void Dispose()=>Directory.Delete(_dir,true);

    [Fact] public void ReadsAllSheetsAndFractionalRatingsAndNullableDate()
    {
        var p=Make("a.xlsx", [("A Show","Смотрю","2.5")], [("A Show","1","2","ep","2024-02-03")], [("A Film","Буду смотреть","4.5","")]);
        var x=new MyShowsWorkbookReader().Read(p);
        Assert.Single(x.Series); Assert.Single(x.Episodes); Assert.Single(x.Movies);
        Assert.Equal(2.5m,x.Series[0].Rating); Assert.Equal(4.5m,x.Movies[0].Rating);
        Assert.Null(x.Movies[0].WatchedDate); Assert.Equal(new DateOnly(2024,2,3),x.Episodes[0].WatchedDate);
    }
    [Theory] [InlineData("3.5")] [InlineData("4.5")]
    public void ReadsHalfPointRatings(string rating){var p=Make("rating.xlsx",[("Show","Перестал",rating)],[],[]);Assert.Equal(decimal.Parse(rating,System.Globalization.CultureInfo.InvariantCulture),new MyShowsWorkbookReader().Read(p).Series[0].Rating);}
    [Fact] public void ReimportSameFileIsIdempotent()
    {
        var p=Make("same.xlsx",[("Show","Смотрю","3")],[("Show","1","1","Pilot","")],[("Film","Полностью посмотрел","4","")]);
        var store=new SqliteStore(Path.Combine(_dir,"db.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);
        importer.Import(p);var second=importer.Import(p);using var c=store.Open();
        Assert.Single(store.GetSeries(c));Assert.Single(store.GetEpisodes(c));Assert.Single(store.GetMovies(c));Assert.Equal(0,second.Run.ImportedCount);Assert.Equal(3,second.Run.SkippedCount);
    }
    [Fact] public void DuplicateTitlesAreNotMergedAndAmbiguousEpisodeIsUnlinked()
    {
        var p=Make("dup.xlsx",[("Professor T.","Смотрю","3"),("Professor T.","Буду смотреть","4")],[("Professor T.","1","1","Pilot",""),("Professor T.","1","1","Pilot","")],[]);
        var store=new SqliteStore(Path.Combine(_dir,"db.sqlite"));store.Initialize();var result=new WorkbookImporter(store).Import(p);using var c=store.Open();
        Assert.Equal(2,store.GetSeries(c).Count);Assert.Equal(2,store.GetEpisodes(c).Count);Assert.All(store.GetEpisodes(c),e=>Assert.Null(e.SeriesId));
        Assert.Equal(2,result.UnresolvedCount);
    }
    [Fact] public void NewWorkbookUpdatesUniqueSeriesInPlaceAndAddsNewTitle()
    {
        var p1=Make("one.xlsx",[("Show","Смотрю","3")],[],[]);var p2=Make("two.xlsx",[("Show","Полностью посмотрел","4.5"),("New","Буду смотреть","")],[],[]);
        Set(p2,"Сериалы",("D2",45), ("E2",8), ("F2",12), ("G2",10.5m), ("H2",8.25m));
        var store=new SqliteStore(Path.Combine(_dir,"db.sqlite"));store.Initialize();var i=new WorkbookImporter(store);i.Import(p1);using(var c=store.Open())Assert.Single(store.GetSeries(c));Guid id;using(var c=store.Open())id=Assert.Single(store.GetSeries(c)).Id;
        var result=i.Import(p2);using var db=store.Open();var shows=store.GetSeries(db);
        Assert.Equal(2,shows.Count);Assert.Equal(1,result.SeriesCounts.Updated);Assert.Equal(1,result.SeriesCounts.Imported);
        var updated=shows.Single(x=>x.Title=="Show");Assert.Equal(id,updated.Id);Assert.Equal("Полностью посмотрел",updated.Status);Assert.Equal(4.5m,updated.Rating);Assert.Equal(8,updated.WatchedEpisodeCount);Assert.Equal(12,updated.RemainingEpisodeCount);Assert.Equal(45,updated.EpisodeDurationMinutes);Assert.Equal(10.5m,updated.WatchedHours);Assert.Equal(8.25m,updated.RemainingHours);
    }

    [Fact] public void MovieStatusRatingDurationAndDateUpdateWithoutChangingLocalId()
    {
        var p1=Make("movie1.xlsx",[],[],[("A Film","Буду смотреть","2.5","")]);
        var p2=Make("movie2.xlsx",[],[],[("A Film","Полностью посмотрел","4.5","2025-01-02")]);Set(p2,"Фильмы",("C2",118));
        var store=new SqliteStore(Path.Combine(_dir,"movie.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(p1);Guid id;using(var c=store.Open())id=Assert.Single(store.GetMovies(c)).Id;
        var result=importer.Import(p2);using var db=store.Open();var movie=Assert.Single(store.GetMovies(db));
        Assert.Equal(1,result.MovieCounts.Updated);Assert.Equal(id,movie.Id);Assert.Equal("Полностью посмотрел",movie.Status);Assert.Equal(4.5m,movie.Rating);Assert.Equal(118,movie.DurationMinutes);Assert.Equal(new DateOnly(2025,1,2),movie.WatchedDate);
    }

    [Fact] public void DuplicateMoviesUseUniqueDurationOrRemainUnresolved()
    {
        var p1=Make("dupm1.xlsx",[],[],[("Same","Буду смотреть","2.5",""),("Same","Буду смотреть","3.5","")]);Set(p1,"Фильмы",("C2",90),("C3",110));
        var p2=Make("dupm2.xlsx",[],[],[("Same","Полностью посмотрел","4.5","2024-01-01"),("Same","Полностью посмотрел","4.5","2024-01-01")]);Set(p2,"Фильмы",("C2",90),("C3",110));
        var store=new SqliteStore(Path.Combine(_dir,"dupm.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(p1);Guid[] ids;using(var c=store.Open())ids=store.GetMovies(c).Select(x=>x.Id).Order().ToArray();
        var result=importer.Import(p2);using var db=store.Open();var movies=store.GetMovies(db);
        Assert.Equal(2,movies.Count);Assert.Equal(2,result.MovieCounts.Updated);Assert.Equal(ids,movies.Select(x=>x.Id).Order().ToArray());
    }

    [Fact] public void DuplicateSeriesUseUniqueEpisodeDurationToUpdateExistingRecords()
    {
        var p1=Make("dups1.xlsx",[("Same","Смотрю","2.5"),("Same","Буду смотреть","3.5")],[],[]);Set(p1,"Сериалы",("D2",42),("D3",55));
        var p2=Make("dups2.xlsx",[("Same","Перестал","4.5"),("Same","Полностью посмотрел","4.5")],[],[]);Set(p2,"Сериалы",("D2",42),("D3",55));
        var store=new SqliteStore(Path.Combine(_dir,"dups.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(p1);Guid[] ids;using(var c=store.Open())ids=store.GetSeries(c).Select(x=>x.Id).Order().ToArray();
        var result=importer.Import(p2);using var db=store.Open();var shows=store.GetSeries(db);
        Assert.Equal(2,shows.Count);Assert.Equal(2,result.SeriesCounts.Updated);Assert.Equal(ids,shows.Select(x=>x.Id).Order().ToArray());Assert.All(shows,x=>Assert.Equal(4.5m,x.Rating));
    }

    [Fact] public void DuplicateTitlesWithoutStableSignalStayUnresolvedWithoutNewDuplicates()
    {
        var p1=Make("amb1.xlsx",[("Same","Смотрю","3"),("Same","Буду смотреть","3")],[],[]);
        var p2=Make("amb2.xlsx",[("Same","Перестал","4"),("Same","Полностью посмотрел","4")],[],[]);
        var store=new SqliteStore(Path.Combine(_dir,"amb.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(p1);Guid[] ids;using(var c=store.Open())ids=store.GetSeries(c).Select(x=>x.Id).Order().ToArray();
        var result=importer.Import(p2);using var db=store.Open();var shows=store.GetSeries(db);
        Assert.Equal(2,shows.Count);Assert.Equal(ids,shows.Select(x=>x.Id).Order().ToArray());Assert.Equal(2,result.SeriesCounts.Skipped);Assert.Equal(2,result.UnresolvedCount);Assert.Contains("unresolved 2",result.Run.ErrorsAndWarnings);
    }

    [Fact] public void EpisodeWatchedDateUpdatesInPlace()
    {
        var p1=Make("episode1.xlsx",[("Show","Смотрю","3")],[("Show","1","1","Pilot","2024-02-03")],[]);
        var p2=Make("episode2.xlsx",[("Show","Смотрю","3")],[("Show","1","1","Pilot","2024-02-04")],[]);
        var store=new SqliteStore(Path.Combine(_dir,"episode.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(p1);Guid id;using(var c=store.Open())id=Assert.Single(store.GetEpisodes(c)).Id;
        var result=importer.Import(p2);using var db=store.Open();var episode=Assert.Single(store.GetEpisodes(db));
        Assert.Equal(1,result.EpisodeCounts.Updated);Assert.Equal(id,episode.Id);Assert.Equal(new DateOnly(2024,2,4),episode.WatchedDate);
    }

    [Fact] public void LibrarySnapshotLoadsStatisticsAndUnresolvedWarnings()
    {
        var path=Make("snapshot.xlsx",[("Twin","Смотрю","3"),("Twin","Буду смотреть","")],[("Twin","1","1","Pilot",""),("Twin","1","2","Second","")],[("Film Twin","Буду смотреть","","") ,("Film Twin","Полностью посмотрел","","")]);
        var store=new SqliteStore(Path.Combine(_dir,"snapshot.sqlite"));store.Initialize();new WorkbookImporter(store).Import(path);
        var snapshot=new LibraryService(store).Load();
        Assert.Equal(2,snapshot.Series.Count);Assert.Equal(2,snapshot.Movies.Count);Assert.Equal(2,snapshot.WatchedEpisodes);
        Assert.Equal(6,snapshot.UnresolvedCount);Assert.Equal(3,snapshot.Unresolved.Count);Assert.NotNull(snapshot.LastImport);
    }

    [Fact] public void CatalogFiltersSearchStatusRatingAndSort()
    {
        var series=new[]{new MyShowsCompanion.Domain.Series(Guid.NewGuid(),"Bravo","Смотрю",4m,null,1,2),new MyShowsCompanion.Domain.Series(Guid.NewGuid(),"Alpha","Буду смотреть",null,null,0,8),new MyShowsCompanion.Domain.Series(Guid.NewGuid(),"Charlie","Смотрю",5m,null,2,1)};
        var movies=new[]{new MyShowsCompanion.Domain.Movie(Guid.NewGuid(),"Bravo Film","Буду смотреть",4m,90,null),new MyShowsCompanion.Domain.Movie(Guid.NewGuid(),"Alpha Film","Полностью посмотрел",null,110,new DateOnly(2024,1,1)),new MyShowsCompanion.Domain.Movie(Guid.NewGuid(),"Charlie Film","Буду смотреть",5m,100,null)};
        Assert.Equal("Charlie",LibraryQueries.FilterSeries(series,"",null,true,CatalogSort.Rating).First().Title);
        Assert.Single(LibraryQueries.FilterSeries(series,"alp","Буду смотреть",false,CatalogSort.Title));
        Assert.Equal("Charlie Film",LibraryQueries.FilterMovies(movies,null,"Буду смотреть",true,CatalogSort.Rating).First().Title);
        Assert.Single(LibraryQueries.FilterMovies(movies,"alpha",null,false,CatalogSort.Title));
        Assert.Equal(2,LibraryQueries.FilterMovies(movies,null,null,true,CatalogSort.Title).Count);
    }

    [Fact] public void UiFacingSnapshotRefreshesAfterImport()
    {
        var first=Make("refresh1.xlsx",[("Unique Show","Смотрю","3")],[],[]);var second=Make("refresh2.xlsx",[("Unique Show","Перестал","4.5")],[],[]);
        var store=new SqliteStore(Path.Combine(_dir,"refresh.sqlite"));store.Initialize();var importer=new WorkbookImporter(store);importer.Import(first);
        var before=new LibraryService(store).Load();var originalId=Assert.Single(before.Series).Id;importer.Import(second);
        var after=new LibraryService(store).Load();var updated=Assert.Single(after.Series);
        Assert.Equal(originalId,updated.Id);Assert.Equal("Перестал",updated.Status);Assert.Equal(4.5m,updated.Rating);Assert.Equal(0,after.WatchedEpisodes);
    }

    private string Make(string name,(string title,string status,string rating)[] series,(string series,string season,string episode,string title,string date)[] episodes,(string title,string status,string rating,string date)[] movies)
    {
        var path=Path.Combine(_dir,name);using var w=new XLWorkbook();
        var s=w.AddWorksheet("Сериалы");var sh=new[]{"Название","Статус","Оценка","Длительность серии, мин.","Просмотрено эпизодов","Осталось эпизодов","Потрачено часов","Осталось часов"};for(var i=0;i<sh.Length;i++)s.Cell(1,i+1).Value=sh[i];for(var r=0;r<series.Length;r++){var x=series[r];s.Cell(r+2,1).Value=x.title;s.Cell(r+2,2).Value=x.status;s.Cell(r+2,3).Value=x.rating;}
        var e=w.AddWorksheet("Эпизоды");var eh=new[]{"Сериал","Сезон","Эпизод","Название","Дата просмотра","Оценка"};for(var i=0;i<eh.Length;i++)e.Cell(1,i+1).Value=eh[i];for(var r=0;r<episodes.Length;r++){var x=episodes[r];e.Cell(r+2,1).Value=x.series;e.Cell(r+2,2).Value=x.season;e.Cell(r+2,3).Value=x.episode;e.Cell(r+2,4).Value=x.title;e.Cell(r+2,5).Value=x.date;}
        var m=w.AddWorksheet("Фильмы");var mh=new[]{"Название","Статус","Длительность фильма, мин.","Дата просмотра","Оценка"};for(var i=0;i<mh.Length;i++)m.Cell(1,i+1).Value=mh[i];for(var r=0;r<movies.Length;r++){var x=movies[r];m.Cell(r+2,1).Value=x.title;m.Cell(r+2,2).Value=x.status;m.Cell(r+2,5).Value=x.rating;m.Cell(r+2,4).Value=x.date;}w.SaveAs(path);return path;
    }

    private static void Set(string path,string sheet,params (string cell,object value)[] cells){using var w=new XLWorkbook(path);foreach(var (cell,value) in cells)w.Worksheet(sheet).Cell(cell).Value=XLCellValue.FromObject(value);w.Save();}
}
