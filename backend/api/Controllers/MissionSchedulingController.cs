﻿using System.Text.Json;
using Api.Controllers.Models;
using Api.Database.Models;
using Api.Services;
using Api.Services.ActionServices;
using Api.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Api.Controllers
{
    [ApiController]
    [Route("missions")]
    public class MissionSchedulingController : ControllerBase
    {
        private readonly IAreaService _areaService;
        private readonly IInstallationService _installationService;
        private readonly ICustomMissionService _customMissionService;
        private readonly IEchoService _echoService;
        private readonly ILogger<MissionSchedulingController> _logger;
        private readonly IMapService _mapService;
        private readonly IMissionDefinitionService _missionDefinitionService;
        private readonly IMissionRunService _missionRunService;
        private readonly ICustomMissionSchedulingService _customMissionSchedulingService;
        private readonly IRobotService _robotService;
        private readonly ISourceService _sourceService;
        private readonly IStidService _stidService;

        public MissionSchedulingController(
            IMissionDefinitionService missionDefinitionService,
            IMissionRunService missionRunService,
            IAreaService areaService,
            IInstallationService installationService,
            IRobotService robotService,
            IEchoService echoService,
            ICustomMissionService customMissionService,
            ILogger<MissionSchedulingController> logger,
            IMapService mapService,
            IStidService stidService,
            ISourceService sourceService,
            ICustomMissionSchedulingService customMissionSchedulingService
        )
        {
            _missionDefinitionService = missionDefinitionService;
            _missionRunService = missionRunService;
            _areaService = areaService;
            _installationService = installationService;
            _robotService = robotService;
            _echoService = echoService;
            _customMissionService = customMissionService;
            _mapService = mapService;
            _stidService = stidService;
            _sourceService = sourceService;
            _missionDefinitionService = missionDefinitionService;
            _customMissionSchedulingService = customMissionSchedulingService;
            _logger = logger;
        }

        /// <summary>
        ///     Schedule an existing mission definition
        /// </summary>
        /// <remarks>
        ///     <para> This query schedules an existing mission and adds it to the database </para>
        /// </remarks>
        [HttpPost("schedule")]
        [Authorize(Roles = Role.User)]
        [ProducesResponseType(typeof(MissionRun), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MissionRun>> Schedule(
            [FromBody] ScheduleMissionQuery scheduledMissionQuery
        )
        {
            var robot = await _robotService.ReadById(scheduledMissionQuery.RobotId);
            if (robot is null)
            {
                return NotFound($"Could not find robot with id {scheduledMissionQuery.RobotId}");
            }

            var missionDefinition = await _missionDefinitionService.ReadById(scheduledMissionQuery.MissionDefinitionId);
            if (missionDefinition == null)
            {
                return NotFound("Mission definition not found");
            }

            List<MissionTask>? missionTasks;
            missionTasks = await _missionDefinitionService.GetTasksFromSource(missionDefinition.Source, missionDefinition.InstallationCode);

            if (missionTasks == null)
            {
                return NotFound("No mission tasks were found for the requested mission");
            }

            var missionRun = new MissionRun
            {
                Name = missionDefinition.Name,
                Robot = robot,
                MissionId = missionDefinition.Id,
                Status = MissionStatus.Pending,
                MissionRunPriority = MissionRunPriority.Normal,
                DesiredStartTime = scheduledMissionQuery.DesiredStartTime,
                Tasks = missionTasks,
                InstallationCode = missionDefinition.InstallationCode,
                Area = missionDefinition.Area,
                Map = new MapMetadata()
            };

            await _mapService.AssignMapToMission(missionRun);

            if (missionRun.Tasks.Any())
            {
                missionRun.CalculateEstimatedDuration();
            }

            var newMissionRun = await _missionRunService.Create(missionRun);

            return CreatedAtAction(nameof(Schedule), new
            {
                id = newMissionRun.Id
            }, newMissionRun);
        }

        /// <summary>
        ///     Schedule a new echo mission
        /// </summary>
        /// <remarks>
        ///     <para> This query schedules a new echo mission and adds it to the database </para>
        /// </remarks>
        [HttpPost]
        [Authorize(Roles = Role.User)]
        [ProducesResponseType(typeof(MissionRun), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MissionRun>> Create(
            [FromBody] ScheduledMissionQuery scheduledMissionQuery
        )
        {
            var robot = await _robotService.ReadById(scheduledMissionQuery.RobotId);
            if (robot is null)
            {
                return NotFound($"Could not find robot with id {scheduledMissionQuery.RobotId}");
            }

            EchoMission? echoMission;
            try
            {
                echoMission = await _echoService.GetMissionById(scheduledMissionQuery.EchoMissionId);
            }
            catch (HttpRequestException e)
            {
                if (e.StatusCode.HasValue && (int)e.StatusCode.Value == 404)
                {
                    _logger.LogWarning(
                        "Could not find echo mission with id={Id}",
                        scheduledMissionQuery.EchoMissionId
                    );
                    return NotFound("Echo mission not found");
                }

                _logger.LogError(e, "Error getting mission from Echo");
                return StatusCode(StatusCodes.Status502BadGateway, $"{e.Message}");
            }
            catch (JsonException e)
            {
                const string Message = "Error deserializing mission from Echo";
                _logger.LogError(e, "{Message}", Message);
                return StatusCode(StatusCodes.Status500InternalServerError, Message);
            }
            catch (InvalidDataException e)
            {
                const string Message =
                    "Can not schedule mission because EchoMission is invalid. One or more tasks does not contain a robot pose";
                _logger.LogError(e, "Message", Message);
                return StatusCode(StatusCodes.Status502BadGateway, Message);
            }

            var missionTasks = echoMission.Tags
                .Select(
                    t =>
                    {
                        var tagPosition = _stidService
                            .GetTagPosition(t.TagId, scheduledMissionQuery.InstallationCode)
                            .Result;
                        return new MissionTask(t, tagPosition);
                    }
                )
                .ToList();

            Area? area = null;
            if (scheduledMissionQuery.AreaName != null)
            {
                area = await _areaService.ReadByInstallationAndName(scheduledMissionQuery.InstallationCode, scheduledMissionQuery.AreaName);
            }

            var source = await _sourceService.CheckForExistingEchoSource(scheduledMissionQuery.EchoMissionId);
            MissionDefinition? existingMissionDefinition = null;
            if (source == null)
            {
                source = new Source
                {
                    SourceId = $"{echoMission.Id}",
                    Type = MissionSourceType.Echo
                };
            }
            else
            {
                var missionDefinitions = await _missionDefinitionService.ReadBySourceId(source.SourceId);
                if (missionDefinitions.Count > 0)
                {
                    existingMissionDefinition = missionDefinitions.First();
                }
            }

            var scheduledMissionDefinition = existingMissionDefinition ?? new MissionDefinition
            {
                Id = Guid.NewGuid().ToString(),
                Source = source,
                Name = echoMission.Name,
                InspectionFrequency = scheduledMissionQuery.InspectionFrequency,
                InstallationCode = scheduledMissionQuery.InstallationCode,
                Area = area
            };

            var missionRun = new MissionRun
            {
                Name = echoMission.Name,
                Robot = robot,
                MissionId = scheduledMissionDefinition.Id,
                Status = MissionStatus.Pending,
                MissionRunPriority = MissionRunPriority.Normal,
                DesiredStartTime = scheduledMissionQuery.DesiredStartTime,
                Tasks = missionTasks,
                InstallationCode = scheduledMissionQuery.InstallationCode,
                Area = area,
                Map = new MapMetadata()
            };

            await _mapService.AssignMapToMission(missionRun);

            if (missionRun.Tasks.Any())
            {
                missionRun.CalculateEstimatedDuration();
            }

            if (existingMissionDefinition == null)
            {
                await _missionDefinitionService.Create(scheduledMissionDefinition);
            }

            var newMissionRun = await _missionRunService.Create(missionRun);

            return CreatedAtAction(nameof(Create), new
            {
                id = newMissionRun.Id
            }, newMissionRun);
        }

        /// <summary>
        ///     Schedule a custom mission
        /// </summary>
        /// <remarks>
        ///     <para> This query schedules a custom mission defined in the incoming json </para>
        /// </remarks>
        [HttpPost]
        [Authorize(Roles = Role.User)]
        [Route("custom")]
        [ProducesResponseType(typeof(MissionRun), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MissionRun>> Create(
            [FromBody] CustomMissionQuery customMissionQuery
        )
        {
            var robot = await _robotService.ReadById(customMissionQuery.RobotId);
            if (robot is null) { return NotFound($"Could not find robot with id {customMissionQuery.RobotId}"); }

            var installation = await _installationService.ReadByName(customMissionQuery.InstallationCode);
            if (installation == null) { return NotFound($"Could not find installation with name {customMissionQuery.InstallationCode}"); }

            var missionTasks = customMissionQuery.Tasks.Select(task => new MissionTask(task)).ToList();

            MissionDefinition? customMissionDefinition;
            try { customMissionDefinition = await _customMissionSchedulingService.FindExistingOrCreateCustomMissionDefinition(customMissionQuery, missionTasks); }
            catch (SourceException e) { return StatusCode(StatusCodes.Status502BadGateway, e.Message); }

            MissionRun? newMissionRun;
            try { newMissionRun = await _customMissionSchedulingService.QueueCustomMissionRun(customMissionQuery, customMissionDefinition.Id, robot.Id, missionTasks); }
            catch (Exception e) when (e is RobotNotFoundException or MissionNotFoundException) { return NotFound(e.Message); }

            return CreatedAtAction(nameof(Create), new
            {
                id = newMissionRun.Id
            }, newMissionRun);
        }
    }
}
