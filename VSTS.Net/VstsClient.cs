﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VSTS.Net.Interfaces;
using VSTS.Net.Models.PullRequests;
using VSTS.Net.Models.Request;
using VSTS.Net.Models.Response;
using VSTS.Net.Models.WorkItems;
using VSTS.Net.Types;
using static VSTS.Net.Utils.NullCheckUtility;

namespace VSTS.Net
{
    public class VstsClient : IVstsClient
    {
        private readonly string _instanceName;
        private readonly IHttpClient _httpClient;
        private readonly ILogger<VstsClient> _logger;

        public VstsClient(string instanceName, IHttpClient client, ILogger<VstsClient> logger)
        {
            _instanceName = instanceName;
            _httpClient = client;
            _logger = logger;
        }

        public VstsClient(string instanceName, IHttpClient client)
            : this(instanceName, client, new NullLogger<VstsClient>())
        {
        }

        /// <inheritdoc />
        public async Task<WorkItemsQueryResult> ExecuteQueryAsync(string project, WorkItemsQuery query)
        {
            ThrowIfArgumentNullOrEmpty(project);
            ThrowIfArgumentNull(query, nameof(query));
            ThrowIfArgumentNullOrEmpty(_instanceName, nameof(_instanceName));
            ThrowIfArgumentNullOrEmpty(query.Query, "Query cannot be empty.");

            var url = VstsUrlBuilder.Create(_instanceName)
                .ForWIQL(project)
                .Build(Constants.CurrentWorkItemsApiVersion);

            _logger.LogDebug("Requesting {0}", url);

            if (query.IsHierarchical)
                return await _httpClient.ExecutePost<HierarchicalWorkItemsQueryResult>(url, query);

            return await _httpClient.ExecutePost<FlatWorkItemsQueryResult>(url, query);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<WorkItem>> GetWorkItemsAsync(string project, WorkItemsQuery query)
        {
            var queryResult = await ExecuteQueryAsync(project, query);
            int[] ids;
            switch (queryResult)
            {
                case FlatWorkItemsQueryResult flat:
                    ids = GetWorkitemIdsFromQuery(flat);
                    break;
                case HierarchicalWorkItemsQueryResult tree:
                    ids = GetWorkitemIdsFromQuery(tree);
                    break;
                default:
                    throw new NotSupportedException($"Query result is of not supported type.");
            }

            var fieldsString = string.Join(",", queryResult.Columns.Select(c => c.ReferenceName));
            var idsString = string.Join(",", ids);
            var url = VstsUrlBuilder.Create(_instanceName)
                .ForWorkItemsBatch(idsString, project)
                .WithQueryParameter("fields", fieldsString)
                .Build();

            var workitemsResponse = await _httpClient.ExecuteGet<CollectionResponse<WorkItem>>(url);

            if (workitemsResponse == null)
                return Enumerable.Empty<WorkItem>();

            return workitemsResponse.Value;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<WorkItemUpdate>> GetWorkItemUpdatesAsync(int workitemId)
        {
            var url = VstsUrlBuilder.Create(_instanceName)
                .ForWorkItems(workitemId)
                .WithSection("updates")
                .Build();

            var result = await _httpClient.ExecuteGet<CollectionResponse<WorkItemUpdate>>(url);
            if (result == null)
                return Enumerable.Empty<WorkItemUpdate>();

            return result.Value;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<PullRequest>> GetPullRequestsAsync(string project, string repository, PullRequestQuery query)
        {
            ThrowIfArgumentNullOrEmpty(project, nameof(project));
            ThrowIfArgumentNullOrEmpty(repository, nameof(repository));

            if (query == null)
                query = PullRequestQuery.None;

            var haveMorePullRequests = true;
            var skip = 0;
            var allPullRequests = new List<PullRequest>();
            while (haveMorePullRequests)
            {
                var url = VstsUrlBuilder.Create(_instanceName)
                    .ForPullRequests(project, repository)
                    .WithQueryParameterIfNotEmpty("searchCriteria.status", query.Status)
                    .WithQueryParameterIfNotEmpty("searchCriteria.reviewerId", query.ReviewerId)
                    .WithQueryParameterIfNotEmpty("searchCriteria.creatorId", query.CreatorId)
                    .WithQueryParameter("$skip", skip)
                    .Build();

                var pullRequestsResponse = await _httpClient.ExecuteGet<CollectionResponse<PullRequest>>(url);
                var pullRequests = pullRequestsResponse?.Value ?? Enumerable.Empty<PullRequest>();

                haveMorePullRequests = pullRequests.Any() && (!query.CreatedAfter.HasValue || pullRequests.Min(p => p.CreationDate) >= query.CreatedAfter);

                if (query.CreatedAfter.HasValue)
                    pullRequests = pullRequests.Where(p => p.CreationDate >= query.CreatedAfter);

                if (query.CustomFilter != null)
                    pullRequests = pullRequests.Where(query.CustomFilter);

                allPullRequests.AddRange(pullRequests);
                
                skip = allPullRequests.Count;
            }

            return allPullRequests;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<PullRequestIteration>> GetPullRequestIterationsAsync(string project, string repository, int pullRequestId)
        {
            ThrowIfArgumentNullOrEmpty(project, nameof(project));
            ThrowIfArgumentNullOrEmpty(repository, nameof(repository));

            var iterationsUrl = VstsUrlBuilder.Create(_instanceName)
              .ForPullRequests(project, repository)
              .WithSection(pullRequestId.ToString())
              .WithSection("iterations")
              .Build();

            var iterationsResponse = await _httpClient.ExecuteGet<CollectionResponse<PullRequestIteration>>(iterationsUrl);
            return iterationsResponse?.Value ?? Enumerable.Empty<PullRequestIteration>();
        }

        /// <inheritdoc />
        public async Task<IEnumerable<PullRequestThread>> GetPullRequestThreadsAsync(string project, string repository, int pullRequestId)
        {
            ThrowIfArgumentNullOrEmpty(project, nameof(project));
            ThrowIfArgumentNullOrEmpty(repository, nameof(repository));

            var threadsUrl = VstsUrlBuilder.Create(_instanceName)
              .ForPullRequests(project, repository)
              .WithSection(pullRequestId.ToString())
              .WithSection("threads")
              .Build();

            var threadsResponse = await _httpClient.ExecuteGet<CollectionResponse<PullRequestThread>>(threadsUrl);
            return threadsResponse?.Value ?? Enumerable.Empty<PullRequestThread>();
        }

        private int[] GetWorkitemIdsFromQuery(FlatWorkItemsQueryResult query)
        {
            return query.WorkItems.Select(w => w.Id).ToArray();
        }

        private int[] GetWorkitemIdsFromQuery(HierarchicalWorkItemsQueryResult query)
        {
            var targetIds = query.WorkItemRelations.Select(w => w.Target.Id);
            var sourceIds = query.WorkItemRelations.Select(w => w.Source.Id);
            return sourceIds.Union(targetIds)
                .Distinct()
                .ToArray();
        }

        public static VstsClient Get(string instanceName, string accessToken, ILogger<VstsClient> logger = null)
        {
            var client = HttpClientUtil.Create(accessToken);
            var httpClient = new DefaultHttpClient(client, new NullLogger<DefaultHttpClient>());
            var clientLogger = logger ?? new NullLogger<VstsClient>();
            return new VstsClient(instanceName, httpClient, logger ?? clientLogger);
        }
    }
}
