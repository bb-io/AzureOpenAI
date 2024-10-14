﻿using System.Text;
using System.Text.RegularExpressions;
using Apps.AzureOpenAI.Api;
using Apps.AzureOpenAI.Models.Dto;
using Apps.AzureOpenAI.Models.Entities;
using Apps.AzureOpenAI.Models.Requests.Chat;
using Apps.AzureOpenAI.Models.Responses.Chat;
using Azure;
using Azure.AI.OpenAI;
using Blackbird.Applications.Sdk.Common;
using Blackbird.Applications.Sdk.Common.Files;
using Blackbird.Applications.Sdk.Common.Invocation;
using Blackbird.Applications.SDK.Extensions.FileManagement.Interfaces;
using Blackbird.Applications.Sdk.Glossaries.Utils.Converters;
using Blackbird.Xliff.Utils;
using Blackbird.Xliff.Utils.Extensions;
using Newtonsoft.Json;
using RestSharp;

namespace Apps.AzureOpenAI.Actions.Base;

public class BaseActions : BaseInvocable
{
    protected readonly OpenAIClient Client;
    protected readonly AzureOpenAiRestClient RestClient;
    protected readonly string DeploymentName;
    protected readonly IFileManagementClient FileManagementClient;

    protected BaseActions(InvocationContext invocationContext, IFileManagementClient fileManagementClient) 
        : base(invocationContext)
    {
        DeploymentName = InvocationContext.AuthenticationCredentialsProviders.First(x => x.KeyName == "deployment")
            .Value;
        Client = new OpenAIClient(
            new Uri(InvocationContext.AuthenticationCredentialsProviders.First(x => x.KeyName == "url").Value),
            new AzureKeyCredential(InvocationContext.AuthenticationCredentialsProviders
                .First(x => x.KeyName == "apiKey").Value));
        RestClient = new(invocationContext.AuthenticationCredentialsProviders);
        FileManagementClient = fileManagementClient;
    }
    
    protected async Task<XliffDocument> DownloadXliffDocumentAsync(FileReference file)
    {
        var fileStream = await FileManagementClient.DownloadAsync(file);
        var xliffMemoryStream = new MemoryStream();
        await fileStream.CopyToAsync(xliffMemoryStream);
        xliffMemoryStream.Position = 0;

        var xliffDocument = xliffMemoryStream.ToXliffDocument();
        if (xliffDocument.TranslationUnits.Count == 0)
        {
            throw new InvalidOperationException("The XLIFF file does not contain any translation units.");
        }

        return xliffDocument;
    }
    
    protected async Task<(string result, UsageDto usage)> ExecuteOpenAIRequestAsync(ExecuteOpenAIRequestParameters parameters)
    {
        var restClient = new RestClient(InvocationContext.AuthenticationCredentialsProviders
            .First(x => x.KeyName == "url").Value);
        var request = new RestRequest("/openai/deployments/" + DeploymentName + $"/chat/completions?api-version={parameters.ApiVersion}", Method.Post);
        
        request.AddHeader("Content-Type", "application/json");
        request.AddHeader("api-key", InvocationContext.AuthenticationCredentialsProviders
            .First(x => x.KeyName == "apiKey").Value);

        var body = new Dictionary<string, object>
        {
            {
                "messages", new List<object>
                {
                    new
                    {
                        role = "system",
                        content = parameters.SystemPrompt
                    },
                    new
                    {
                        role = "user",
                        content = parameters.Prompt
                    }
                }
            },
        };
        
        if(parameters.ResponseFormat != null)
        {
            body.Add("response_format", parameters.ResponseFormat);
        }
        
        if(parameters.ChatRequest?.Temperature != null)
        {
            body.Add("temperature", parameters.ChatRequest.Temperature.Value);
        }
        
        if(parameters.ChatRequest?.MaximumTokens != null)
        {
            body.Add("max_completion_tokens", parameters.ChatRequest.MaximumTokens.Value);
        }
        
        if(parameters.ChatRequest?.PresencePenalty != null)
        {
            body.Add("presence_penalty", parameters.ChatRequest.PresencePenalty.Value);
        }
        
        if(parameters.ChatRequest?.FrequencyPenalty != null)
        {
            body.Add("frequency_penalty", parameters.ChatRequest.FrequencyPenalty.Value);
        }
        
        request.AddJsonBody(body);
        
        var response = await restClient.ExecuteAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = JsonConvert.DeserializeObject<ErrorDto>(response.Content!)!;
            throw new InvalidOperationException(error.ToString());
        }
        
        var chatResponse = JsonConvert.DeserializeObject<OpenAIResponseDto>(response.Content!)!;
        return (chatResponse.Choices.First().Message.Content, new(chatResponse.Usage));
    }
    
    protected async Task<string> GetGlossaryPromptPart(FileReference glossary, string sourceContent)
    {
        var glossaryStream = await FileManagementClient.DownloadAsync(glossary);
        var blackbirdGlossary = await glossaryStream.ConvertFromTbx();

        var glossaryPromptPart = new StringBuilder();
        glossaryPromptPart.AppendLine();
        glossaryPromptPart.AppendLine();
        glossaryPromptPart.AppendLine("Glossary entries (each entry includes terms in different language. Each " +
                                      "language may have a few synonymous variations which are separated by ;;):");

        var entriesIncluded = false;
        foreach (var entry in blackbirdGlossary.ConceptEntries)
        {
            var allTerms = entry.LanguageSections.SelectMany(x => x.Terms.Select(y => y.Term));
            if (!allTerms.Any(x => Regex.IsMatch(sourceContent, $@"\b{x}\b", RegexOptions.IgnoreCase))) continue;
            entriesIncluded = true;

            glossaryPromptPart.AppendLine();
            glossaryPromptPart.AppendLine("\tEntry:");

            foreach (var section in entry.LanguageSections)
            {
                glossaryPromptPart.AppendLine(
                    $"\t\t{section.LanguageCode}: {string.Join(";; ", section.Terms.Select(term => term.Term))}");
            }
        }

        return entriesIncluded ? glossaryPromptPart.ToString() : null;
    }

}