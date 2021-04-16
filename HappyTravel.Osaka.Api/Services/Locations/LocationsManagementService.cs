﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using HappyTravel.MultiLanguage;
using HappyTravel.Osaka.Api.Infrastructure;
using HappyTravel.Osaka.Api.Infrastructure.Extensions;
using HappyTravel.Osaka.Api.Infrastructure.Logging;
using HappyTravel.Osaka.Api.Models;
using HappyTravel.Osaka.Api.Models.Elasticsearch;
using HappyTravel.Osaka.Api.Services.HttpClients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nest;
using IndexOptions = HappyTravel.Osaka.Api.Options.IndexOptions;
using Location = HappyTravel.Osaka.Api.Models.Location;
using Result = CSharpFunctionalExtensions.Result;

namespace HappyTravel.Osaka.Api.Services.Locations
{
    public class LocationsManagementService : ILocationsManagementService
    {
        public LocationsManagementService(IElasticClient elasticClient, IMapperHttpClient mapperHttpClient, IOptions<IndexOptions> indexOptions, ILogger<LocationsManagementService> logger)
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _indexOptions = indexOptions.Value;
            _mapperHttpClient = mapperHttpClient;
        }

        
        public async Task<Result<int>> ReUpload(CancellationToken cancellationToken = default)
        {
            _logger.LogStartUploadingLocations("Start locations upload");

            var languageCode = LanguagesHelper.GetLanguageCode(Languages.English).ToLowerInvariant();
            if (!ElasticsearchHelper.TryGetIndex(_indexOptions.Indexes!, languageCode, out var index))
            {
                var error = $"Index with the language '{languageCode}' doesn't exist";
                _logger.LogUploadingError(error);
                return Result.Failure<int>(error);
            }

            _logger.LogRemoveLocationsFromIndex($"Remove all locations from the Elasticsearch index '{index}'");
            
            var (_, removeFailure, removeError) = await ReCreateIndex(index!, cancellationToken);
            if (removeFailure)
            {
                _logger.LogUploadingError(removeError);
                return Result.Failure<int>(removeError);
            }

            var locationsUploaded = 0;
            
            foreach (var locationType in Enum.GetValues<MapperLocationTypes>())
            {
                const int batchSize = 10000;
                await foreach (var (_, isFailure, locations, error) in GetFromMapper(locationType, languageCode, batchSize, cancellationToken))
                {
                    if (isFailure)
                    {
                        _logger.LogUploadingError(error);
                        return Result.Failure<int>(error);
                    }

                    _logger.LogGetLocationsFromMapper($"'{locations.Count}' locations received from the mapper");
                    
                    if (!locations.Any())
                        continue;
                    
                    var (_, uploadFailure, uploadError) = await Add(locations, index, cancellationToken);
                    if (uploadFailure)
                    {
                        _logger.LogUploadingError(uploadError);
                        return Result.Failure<int>(uploadError);
                    }
                    
                    locationsUploaded += locations.Count;
                }
            }

            _logger.LogCompleteUploadingLocations($"Uploading to the Elasticsearch index '{index}' has been completed. The total number of uploaded locations is '{locationsUploaded}'");
            
            return Result.Success(locationsUploaded);
        }

        
        public async Task<Result> Add(List<Location> locations, string index, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient.BulkAsync(b
                => b.IndexMany(Build(locations)), cancellationToken);
            
            _logger.LogAddLocations($"'{locations.Count}' locations have been added to the index '{index}'");
            
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);

            LogErrorsIfNeeded(response);
              
            return Result.Success();
        }

      
        public async Task<Result> Update(List<Location> locations, string index, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient
                .BulkAsync(b 
                    => b.Index(index)
                        .UpdateMany(Build(locations), (bd, l) 
                            => bd.Id(l.HtId).Doc(l)), cancellationToken);
            
            _logger.LogUpdateLocations($"'{locations.Count}' locations have been updated in the index '{index}'");
            
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);

            LogErrorsIfNeeded(response);
              
            return Result.Success();
        }
        
        
        public async Task<Result> Remove(List<Location> locations, string index, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient.BulkAsync(b 
                => b.Index(index)
                    .DeleteMany(Build(locations), (bd, l) 
                        => bd.Document(l)), cancellationToken);
            
            _logger.LogUpdateLocations($"'{locations.Count}' locations have been removed from the index '{index}'");
            
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);

            LogErrorsIfNeeded(response);
              
            return Result.Success();
        }
        
        
        public async Task<Result> Add(Location location, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient.IndexDocumentAsync(Build(location), cancellationToken);
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);
            
            return Result.Success();
        }

        
        public async Task<Result> Update(Location location, string index, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient.UpdateAsync<Location, object>(DocumentPath<Location>.Id(location.HtId), u 
                => u.Index(index).Doc(Build(location)), cancellationToken);
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);
            
            return Result.Success();
        }
        
        
        public async Task<Result> Remove(Location location, string index, CancellationToken cancellationToken = default)
        {
            var response = await _elasticClient.DeleteAsync(new DeleteRequest(index, location.HtId), cancellationToken);
            if (!response.IsValid)
                return Result.Failure(response.ApiCall?.DebugInformation);
            
            return Result.Success();
        }

        
        private void LogErrorsIfNeeded(BulkResponse response)
        {
            if (response.Errors)
            {
                var sb = new StringBuilder();
                foreach (var itemWithError in response.ItemsWithErrors)
                {
                    sb.AppendLine($"Failed to index location {itemWithError.Id}: {itemWithError.Error}");
                }
                
                _logger.LogUploadingError($"{sb} {nameof(response.DebugInformation)}: {response.DebugInformation}");
            }
        }
        
        
        private async IAsyncEnumerable<Result<List<Location>>> GetFromMapper(MapperLocationTypes locationType, string languageCode, int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var skip = 0;
            List<Location> locations;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isFailure;
                string error;
                (_, isFailure, locations, error) = await _mapperHttpClient.GetLocations(locationType, languageCode, default, skip, batchSize, cancellationToken);
                if (isFailure)
                    yield return Result.Failure<List<Location>>(error);
                
                yield return locations;
                
                skip += batchSize;
            } while (locations.Count == batchSize);
        }
        
       
        private async Task<Result> ReCreateIndex(string index, CancellationToken cancellationToken = default)
        {
            await _elasticClient.Indices.DeleteAsync(index, ct: cancellationToken);
            var response = await _elasticClient.CreateIndexes(_indexOptions.Indexes);
            
            if (response.OriginalException is not null)
                throw response.OriginalException;
            
            return !response.IsValid
                ? Result.Failure(response.ToString()) 
                : Result.Success();
        }
        
        
        private List<Models.Elasticsearch.Location> Build(List<Location> locations)
            => locations.Select(Build).ToList();


        private Models.Elasticsearch.Location Build(Location location)
        {
            var uploadedDate = DateTime.UtcNow;
            return new()
            {
                Id = location.HtId,
                Name = location.Name,
                Locality = location.Locality,
                Country = location.Country,
                CountryCode = location.CountryCode,
                Suggestion = BuildSuggestion(location),
                PredictionText = BuildPredictionText(location),
                Coordinates = new GeoCoordinate(location.Coordinates.Latitude, location.Coordinates.Longitude),
                DistanceInMeters = location.DistanceInMeters,
                LocationType = location.LocationType.ToString().ToLowerInvariant(),
                Type = location.Type,
                Modified = uploadedDate
            };
        }


        private static Suggestion BuildSuggestion(Location location)
        {
            var suggest = new Suggestion();

            suggest.Input = location.LocationType switch
            {
                MapperLocationTypes.Country => new List<string> {location.Country},
                MapperLocationTypes.Locality => new List<string>
                {
                    $"{location.Locality}",
                    $"{location.Country} {location.Locality}",
                    $"{location.Locality} {location.Country}"
                },
                MapperLocationTypes.Accommodation => new List<string>
                {
                    $"{location.Name}",
                    $"{location.Name} {location.Locality} {location.Country}",
                    $"{location.Name} {location.Country} {location.Locality}",
                    $"{location.Locality} {location.Name}",
                    $"{location.Locality} {location.Country} {location.Name}",
                    $"{location.Country} {location.Name}",
                    $"{location.Country} {location.Locality} {location.Name}",
                },
                _ => suggest.Input
            };

            return suggest;
        }
        
        
        private static string BuildPredictionText(Location location)
        {
            var result = location.LocationType == MapperLocationTypes.Accommodation
                ? location.Name
                : string.Empty;
            
            if (!string.IsNullOrEmpty(location.Locality))
                result += string.IsNullOrEmpty(result) ? location.Locality : ", " + location.Locality;
                
            if (!string.IsNullOrEmpty(location.Country))
                result += string.IsNullOrEmpty(result)? location.Country : ", " + location.Country;

            return result;
        }
        

        private readonly IMapperHttpClient _mapperHttpClient;
        private readonly IElasticClient _elasticClient;
        private readonly IndexOptions _indexOptions;
        private readonly ILogger<LocationsManagementService> _logger;
    }
}