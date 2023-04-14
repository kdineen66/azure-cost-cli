using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Spectre.Console;
using Spectre.Console.Cli;

public class ShowCommand : AsyncCommand<ShowSettings>
{
    private readonly HttpClient _client;
    private readonly Dictionary<OutputFormat, OutputFormatter> _outputFormatters = new();

    public ShowCommand()
    {
        // Setup the http client
        var handler = new HttpClientHandler();
        handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        _client = new HttpClient(handler);
        _client.BaseAddress = new Uri("https://management.azure.com/");

        // Add the output formatters
        _outputFormatters.Add(OutputFormat.Console, new ConsoleOutputFormatter());
        _outputFormatters.Add(OutputFormat.Json, new JsonOutputFormatter());
    }

    public override ValidationResult Validate(CommandContext context, ShowSettings settings)
    {
        // Validate if the timeframe is set to Custom, then the from and to dates must be specified and the from date must be before the to date
        if (settings.Timeframe == TimeframeType.Custom)
        {
            if (settings.From == null)
            {
                return ValidationResult.Error("The from date must be specified when the timeframe is set to Custom.");
            }

            if (settings.To == null)
            {
                return ValidationResult.Error("The to date must be specified when the timeframe is set to Custom.");
            }

            if (settings.From > settings.To)
            {
                return ValidationResult.Error("The from date must be before the to date.");
            }
        }

        return ValidationResult.Success();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ShowSettings settings)
    {
        await RetrieveToken();

        // Get the subscription ID from the settings
        var subscriptionId = settings.Subscription;

        if (subscriptionId == Guid.Empty)
        {
            // Get the subscription ID from the Azure CLI
            try
            {
                subscriptionId = Guid.Parse(GetDefaultAzureSubscriptionId());
                settings.Subscription = subscriptionId;
            }
            catch (Exception e)
            {
                AnsiConsole.WriteException(new ArgumentException(
                    "Missing subscription ID. Please specify a subscription ID or login to Azure CLI.", e));
                return -1;
            }
        }

        // Fetch the costs
        var costs = await RetrieveCosts(settings.Output == OutputFormat.Console, subscriptionId, settings.Timeframe,
            settings.From, settings.To);
        var forecastedCosts = await RetrieveForecastedCosts(settings.Output == OutputFormat.Console, subscriptionId);
        var byServiceNameCosts = await RetrieveCostByServiceName(settings.Output == OutputFormat.Console,
            subscriptionId, settings.Timeframe, settings.From, settings.To);
        var byLocationCosts = await RetrieveCostByLocation(settings.Output == OutputFormat.Console, subscriptionId,
            settings.Timeframe, settings.From, settings.To);

        // Write the output
        await _outputFormatters[settings.Output]
            .WriteOutput(settings, costs, forecastedCosts, byServiceNameCosts, byLocationCosts);

        return 0;
    }

    private async Task RetrieveToken()
    {
        // Get the token by using the DefaultAzureCredential
        var tokenCredential = new ChainedTokenCredential(
            new AzureCliCredential(),
            new DefaultAzureCredential());
        var token = await tokenCredential.GetTokenAsync(new TokenRequestContext(new[]
            { $"https://management.azure.com/.default" }));

        // Set as the bearer token for the HTTP client
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }


    private async Task<IEnumerable<CostItem>> RetrieveCosts(bool canWriteToConsole, Guid subscriptionId,
        TimeframeType timeFrame, DateOnly from, DateOnly to)
    {
        var uri = new Uri(
            $"/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000",
            UriKind.Relative);

        var payload = new
        {
            type = "ActualCost",
            timeframe = timeFrame.ToString(),
            timePeriod = timeFrame == TimeframeType.Custom
                ? new
                {
                    from = from.ToString("yyyy-MM-dd"),
                    to = to.ToString("yyyy-MM-dd")
                }
                : null,
            dataSet = new
            {
                granularity = "Daily",
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "Cost",
                        function = "Sum"
                    },
                    totalCostUSD = new
                    {
                        name = "CostUSD",
                        function = "Sum"
                    }
                },
                sorting = new[]
                {
                    new
                    {
                        direction = "Ascending",
                        name = "UsageDate"
                    }
                }
            }
        };

        var response = await _client.PostAsJsonAsync(uri, payload);
        response.EnsureSuccessStatusCode();

        CostQueryResponse? content = await response.Content.ReadFromJsonAsync<CostQueryResponse>();

        var items = new List<CostItem>();
        foreach (var row in content.properties.rows)
        {
            var date = DateOnly.ParseExact(row[2].ToString(), "yyyyMMdd", CultureInfo.InvariantCulture);
            var value = double.Parse(row[0].ToString(), CultureInfo.InvariantCulture);
            var valueUsd = double.Parse(row[1].ToString(), CultureInfo.InvariantCulture);

            var currency = row[3].ToString();

            var costItem = new CostItem(date, value, valueUsd, currency);
            items.Add(costItem);
        }

        return items;
    }

    private async Task<IEnumerable<CostNamedItem>> RetrieveCostByServiceName(bool canWriteToConsole,
        Guid subscriptionId, TimeframeType timeFrame, DateOnly from, DateOnly to)
    {
        var uri = new Uri(
            $"/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000",
            UriKind.Relative);

        var payload = new
        {
            type = "ActualCost",
            timeframe = timeFrame.ToString(),
            timePeriod = timeFrame == TimeframeType.Custom
                ? new
                {
                    from = from.ToString("yyyy-MM-dd"),
                    to = to.ToString("yyyy-MM-dd")
                }
                : null,
            dataSet = new
            {
                granularity = "None",
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "Cost",
                        function = "Sum"
                    },
                    totalCostUSD = new
                    {
                        name = "CostUSD",
                        function = "Sum"
                    }
                },
                sorting = new[]
                {
                    new
                    {
                        direction = "Ascending",
                        name = "UsageDate"
                    }
                },
                grouping = new[]
                {
                    new
                    {
                        type = "Dimension",
                        name = "ServiceName"
                    }
                },
                filter = new
                {
                    Dimensions = new
                    {
                        Name = "PublisherType",
                        Operator = "In",
                        Values = new[] { "azure" }
                    }
                }
            }
        };
        var response = await _client.PostAsJsonAsync(uri, payload);
        response.EnsureSuccessStatusCode();

        CostQueryResponse? content = await response.Content.ReadFromJsonAsync<CostQueryResponse>();

        var items = new List<CostNamedItem>();
        foreach (var row in content.properties.rows)
        {
            var serviceName = row[2].ToString();
            var value = double.Parse(row[0].ToString(), CultureInfo.InvariantCulture);
            var valueUsd = double.Parse(row[1].ToString(), CultureInfo.InvariantCulture);

            var currency = row[3].ToString();

            var costItem = new CostNamedItem(serviceName, value, valueUsd, currency);
            items.Add(costItem);
        }

        return items;
    }

    private async Task<IEnumerable<CostNamedItem>> RetrieveCostByLocation(bool canWriteToConsole, Guid subscriptionId,
        TimeframeType timeFrame, DateOnly from, DateOnly to)
    {
        var uri = new Uri(
            $"/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000",
            UriKind.Relative);

        var payload = new
        {
            type = "ActualCost",
            timeframe = timeFrame.ToString(),
            timePeriod = timeFrame == TimeframeType.Custom
                ? new
                {
                    from = from.ToString("yyyy-MM-dd"),
                    to = to.ToString("yyyy-MM-dd")
                }
                : null,
            dataSet = new
            {
                granularity = "None",
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "Cost",
                        function = "Sum"
                    },
                    totalCostUSD = new
                    {
                        name = "CostUSD",
                        function = "Sum"
                    }
                },
                sorting = new[]
                {
                    new
                    {
                        direction = "Ascending",
                        name = "UsageDate"
                    }
                },
                grouping = new[]
                {
                    new
                    {
                        type = "Dimension",
                        name = "ResourceLocation"
                    }
                },
                filter = new
                {
                    Dimensions = new
                    {
                        Name = "PublisherType",
                        Operator = "In",
                        Values = new[] { "azure" }
                    }
                }
            }
        };
        var response = await _client.PostAsJsonAsync(uri, payload);
        response.EnsureSuccessStatusCode();

        CostQueryResponse? content = await response.Content.ReadFromJsonAsync<CostQueryResponse>();

        var items = new List<CostNamedItem>();
        foreach (var row in content.properties.rows)
        {
            var location = row[2].ToString();
            var value = double.Parse(row[0].ToString(), CultureInfo.InvariantCulture);
            var valueUsd = double.Parse(row[1].ToString(), CultureInfo.InvariantCulture);

            var currency = row[3].ToString();

            var costItem = new CostNamedItem(location, value, valueUsd, currency);
            items.Add(costItem);
        }

        return items;
    }

    private async Task<IEnumerable<CostItem>> RetrieveForecastedCosts(bool canWriteToConsole, Guid subscriptionId)
    {
        var uri = new Uri(
            $"/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/forecast?api-version=2021-10-01&$top=5000",
            UriKind.Relative);

        var payload = new
        {
            type = "ActualCost",

            dataSet = new
            {
                granularity = "Daily",
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "Cost",
                        function = "Sum"
                    }
                },
                sorting = new[]
                {
                    new
                    {
                        direction = "Ascending",
                        name = "UsageDate"
                    }
                },
                filter = new
                {
                    Dimensions = new
                    {
                        Name = "PublisherType",
                        Operator = "In",
                        Values = new[] { "azure" }
                    }
                }
            }
        };
        var response = await _client.PostAsJsonAsync(uri, payload);
        response.EnsureSuccessStatusCode();

        CostQueryResponse? content = await response.Content.ReadFromJsonAsync<CostQueryResponse>();

        var items = new List<CostItem>();
        foreach (var row in content.properties.rows)
        {
            var date = DateOnly.ParseExact(row[1].ToString(), "yyyyMMdd", CultureInfo.InvariantCulture);
            var value = double.Parse(row[0].ToString(), CultureInfo.InvariantCulture);

            var currency = row[3].ToString();

            var costItem = new CostItem(date, value, value, currency);
            items.Add(costItem);
        }

        return items;
    }

    static string GetDefaultAzureSubscriptionId()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "az",
            Arguments = "account show",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = new Process { StartInfo = startInfo })
        {
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                throw new Exception($"Error executing 'az account show': {error}");
            }

            using (var jsonDocument = JsonDocument.Parse(output))
            {
                JsonElement root = jsonDocument.RootElement;
                if (root.TryGetProperty("id", out JsonElement idElement))
                {
                    string subscriptionId = idElement.GetString();
                    return subscriptionId;
                }
                else
                {
                    throw new Exception("Unable to find the 'id' property in the JSON output.");
                }
            }
        }
    }
}