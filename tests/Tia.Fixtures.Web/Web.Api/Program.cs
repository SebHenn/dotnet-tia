using Web.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

// Registered by type and resolved by the container: nothing in the solution constructs it, so its
// constructor reaches the tests only through this line and the factory that runs it (#40).
builder.Services.AddSingleton<IGreeting, Greeting>();

var app = builder.Build();

// A plain literal template.
app.MapGet("/contributors", Contributors.List);

// A template with a parameter segment, which no exact string match can meet.
app.MapGet("/contributors/{id}", (int id) => Contributors.ById(id));

// A group prefix, so the route the test calls exists in no single literal anywhere.
var api = app.MapGroup("/api");
api.MapGet(Routes.Count, Contributors.Count);

app.MapGet("/greeting", (IGreeting greeting) => greeting.Text);

app.MapControllers();

app.Run();

/// <summary>Named so the functional tests can host this app in process.</summary>
public partial class Program;
