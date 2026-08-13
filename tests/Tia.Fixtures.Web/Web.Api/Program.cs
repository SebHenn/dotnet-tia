using Web.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

var app = builder.Build();

// A plain literal template.
app.MapGet("/contributors", Contributors.List);

// A template with a parameter segment, which no exact string match can meet.
app.MapGet("/contributors/{id}", (int id) => Contributors.ById(id));

// A group prefix, so the route the test calls exists in no single literal anywhere.
var api = app.MapGroup("/api");
api.MapGet(Routes.Count, Contributors.Count);

app.MapControllers();

app.Run();

/// <summary>Named so the functional tests can host this app in process.</summary>
public partial class Program;
