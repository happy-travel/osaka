﻿using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using HappyTravel.PredictionService.Filters.Authorization;
using HappyTravel.PredictionService.Services.Locations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HappyTravel.PredictionService.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/{v:apiVersion}/locations")]
    [Produces("application/json")]
    [Authorize(Policy = Policies.OnlyManagerClient)]
    public class LocationsManagementController : BaseController
    {
        public LocationsManagementController(ILocationsManagementService locationsManagementService)
        {
            _locationsManagementService = locationsManagementService;
        }

        
        /// <summary>
        /// Re-uploads locations from the mapper
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Number of locations</returns>
        [HttpPost("re-upload")]
        [ProducesResponseType(typeof(int), (int) HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ProblemDetails), (int) HttpStatusCode.BadRequest)]
        public async Task<IActionResult> ReUpload(CancellationToken cancellationToken = default)
        {
            var (_, isFailure, uploaded, error) = await _locationsManagementService.ReUpload(cancellationToken);

            return !isFailure
                ? Ok($"Locations uploaded '{uploaded}'")
                : BadRequestWithProblemDetails(error);
        }

        
        private readonly ILocationsManagementService _locationsManagementService;
    }
}