﻿using System;
using System.Diagnostics;
using Latios.Psyshock;
using Latios.Transforms;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Physics = Latios.Psyshock.Physics;

namespace Latios.FlowFieldNavigation
{
    /// <summary>
    /// Configuration structure for building a field.
    /// </summary>
    public struct BuildFieldConfig
    {
        internal FieldSettings FieldSettings;
        internal TransformQvvs Transform;
        internal CollisionLayer ObstaclesLayer;
        internal CollisionLayerSettings ObstaclesLayerSettings;
        internal BuildAgentsConfig AgentsConfig;

        internal bool HasObstaclesLayer;
        internal bool HasAgentsQuery;
    }
    
    /// <summary>
    /// Configuration structure for agent influence on field.
    /// </summary>
    public struct BuildAgentsConfig
    {
        internal EntityQuery AgentsQuery;
        internal FlowFieldAgentsTypeHandles AgentTypeHandles;
    }

    /// <summary>
    /// Provides functionality for building and configuring FlowFields for navigation.
    /// </summary>
    public static partial class FlowField
    {
        /// <summary>
        /// Creates a new default flow field configuration.
        /// </summary>
        /// <returns>Default configuration with identity transform</returns>
        public static BuildFieldConfig BuildField() => new()
        {
            FieldSettings = FieldSettings.Default,
            Transform = TransformQvvs.identity
        };

        #region FluentChain
        
        /// <summary>
        /// Sets custom field settings for the configuration.
        /// </summary>
        /// <param name="config">Configuration to modify</param>
        /// <param name="settings">New field settings</param>
        /// <returns>Modified configuration</returns>
        public static BuildFieldConfig WithSettings(this BuildFieldConfig config, FieldSettings settings)
        {
            config.FieldSettings = settings;
            return config;
        }

        /// <summary>
        /// Sets the transform for the field.
        /// </summary>
        /// <param name="config">Configuration to modify</param>
        /// <param name="transform">New transform</param>
        /// <returns>Modified configuration</returns>
        public static BuildFieldConfig WithTransform(this BuildFieldConfig config, TransformQvvs transform)
        {
            config.Transform = transform;
            return config;
        }

        /// <summary>
        /// Configures obstacles for the field.
        /// </summary>
        /// <param name="config">Configuration to modify</param>
        /// <param name="obstaclesLayer">Collision layer containing obstacles</param>
        /// <param name="obstaclesLayerSettings">Settings from obstaclesLayer</param>
        /// <returns>Modified configuration</returns>
        public static BuildFieldConfig WithObstacles(this BuildFieldConfig config, in CollisionLayer obstaclesLayer, CollisionLayerSettings obstaclesLayerSettings)
        {
            config.HasObstaclesLayer = true;
            config.ObstaclesLayer = obstaclesLayer;
            config.ObstaclesLayerSettings = obstaclesLayerSettings;
            return config;
        }

        /// <summary>
        /// Configures agents that influence the field.
        /// </summary>
        /// <param name="config">Configuration to modify</param>
        /// <param name="agentsQuery">Query to find agents</param>
        /// <param name="agentsHandles">Type handles for agent components</param>
        /// <returns>Modified configuration</returns>
        public static BuildFieldConfig WithAgents(this BuildFieldConfig config, EntityQuery agentsQuery, in FlowFieldAgentsTypeHandles agentsHandles)
        {
            config.HasAgentsQuery = true;
            config.AgentsConfig = new BuildAgentsConfig
            {
                AgentsQuery = agentsQuery,
                AgentTypeHandles = agentsHandles,
            };
            return config;
        }

        /// <summary>
        /// Creates configuration for updating agent influence on an existing field.
        /// Use this when obstacles haven't changed but agents have moved.
        /// </summary>
        /// <param name="agentsQuery">Query to find agents</param>
        /// <param name="agentsHandles">Type handles for agent components</param>
        /// <returns>New agent influence configuration</returns>
        public static BuildAgentsConfig UpdateAgentsInfluence(EntityQuery agentsQuery, in FlowFieldAgentsTypeHandles agentsHandles) => new()
        {
            AgentsQuery = agentsQuery, AgentTypeHandles = agentsHandles,
        };

        #endregion

        #region Schedulers

        /// <summary>
        /// Schedules parallel jobs to build a field with the current configuration.
        /// </summary>
        /// <param name="config">Configuration for building the field</param>
        /// <param name="field">Output parameter for the created field</param>
        /// <param name="allocator">Memory allocator to use for field data</param>
        /// <param name="inputDeps">Optional input job dependencies</param>
        /// <returns>JobHandle representing the scheduled jobs</returns>
        /// <remarks>
        /// This method schedules the following parallel jobs:
        /// 1. Builds collision bodies for all field cells.
        /// 2. Processes obstacles layer if configured.
        /// 3. Processes agents influence if configured.
        /// </remarks>
        public static JobHandle ScheduleParallel(this BuildFieldConfig config, out Field field, AllocatorManager.AllocatorHandle allocator, JobHandle inputDeps = default)
        {
            config.ValidateSettings();
            field = new Field(config.FieldSettings, config.Transform, allocator);

            var dependency = inputDeps;
            dependency = new FlowFieldInternal.BuildCellsBodiesJob { Field = field }.ScheduleParallel(field.CellColliders.Length, 32, dependency);
            dependency = config.ProcessObstaclesLayer(in field, dependency);
            if (!config.HasAgentsQuery) return dependency;
            dependency = ScheduleParallel(config.AgentsConfig, in field, dependency);
            return dependency;
        }
        
        /// <summary>
        /// Schedules single-threaded jobs to build a field with the current configuration.
        /// </summary>
        /// <param name="config">Configuration for building the field</param>
        /// <param name="field">Output parameter for the created field</param>
        /// <param name="allocator">Memory allocator to use for field data</param>
        /// <param name="inputDeps">Optional input job dependencies</param>
        /// <returns>JobHandle representing the scheduled jobs</returns>
        /// <remarks>
        /// This method schedules the following single-threaded jobs:
        /// 1. Builds collision bodies for all field cells.
        /// 2. Processes obstacles layer if configured.
        /// 3. Processes agents influence if configured.
        /// </remarks>
        public static JobHandle Schedule(this BuildFieldConfig config, out Field field, AllocatorManager.AllocatorHandle allocator, JobHandle inputDeps = default)
        {
            config.ValidateSettings();
            field = new Field(config.FieldSettings, config.Transform, allocator);

            var dependency = inputDeps;
            dependency = new FlowFieldInternal.BuildCellsBodiesJob { Field = field }.Schedule(field.CellColliders.Length, dependency);
            dependency = config.ProcessObstaclesLayer(in field, dependency);
            if (!config.HasAgentsQuery) return dependency;
            dependency = Schedule(config.AgentsConfig, in field, dependency);
            return dependency;
        }

        /// <summary>
        /// Schedules parallel jobs to update agent influences on an existing field.
        /// </summary>
        /// <param name="config">Agents configuration</param>
        /// <param name="field">Existing field to update</param>
        /// <param name="inputDeps">Optional input job dependencies</param>
        /// <returns>JobHandle representing the scheduled jobs</returns>
        public static JobHandle ScheduleParallel(this BuildAgentsConfig config, in Field field, JobHandle inputDeps = default)
        {
            var dependency = inputDeps;
            var agentsCount = config.AgentsQuery.CalculateEntityCount();
            var capacity = agentsCount * 4;
            var densityHashMap = new NativeParallelMultiHashMap<int, float3>(capacity, Allocator.TempJob);

            dependency = new FlowFieldInternal.AgentsInfluenceJob
            {
                DensityHashMap = densityHashMap.AsParallelWriter(),
                Field = field,
                TypeHandles = config.AgentTypeHandles
            }.ScheduleParallel(config.AgentsQuery, dependency);

            dependency = new FlowFieldInternal.AgentsPostProcessJob
            {
                DensityHashMap = densityHashMap,
                DensityMap = field.DensityMap,
                MeanVelocityMap = field.MeanVelocityMap,
            }.ScheduleParallel(field.DensityMap.Length, 32, dependency);

            dependency = densityHashMap.Dispose(dependency);
            return dependency;
        }

        /// <summary>
        /// Schedules single-threaded jobs to update agent influences on an existing field.
        /// </summary>
        /// <param name="config">Agent influence configuration</param>
        /// <param name="field">Existing field to update</param>
        /// <param name="inputDeps">Optional input job dependencies</param>
        /// <returns>JobHandle representing the scheduled jobs</returns>
        public static JobHandle Schedule(this BuildAgentsConfig config, in Field field, JobHandle inputDeps = default)
        {
            var dependency = inputDeps;
            var agentsCount = config.AgentsQuery.CalculateEntityCount();
            var capacity = agentsCount * 4;
            var densityHashMap = new NativeParallelMultiHashMap<int, float3>(capacity, Allocator.TempJob);

            dependency = new FlowFieldInternal.AgentsInfluenceJob
            {
                DensityHashMap = densityHashMap.AsParallelWriter(),
                Field = field,
                TypeHandles = config.AgentTypeHandles
            }.Schedule(config.AgentsQuery, dependency);

            dependency = new FlowFieldInternal.AgentsPostProcessJob
            {
                DensityHashMap = densityHashMap,
                DensityMap = field.DensityMap,
                MeanVelocityMap = field.MeanVelocityMap,
            }.Schedule(field.DensityMap.Length, dependency);

            dependency = densityHashMap.Dispose(dependency);
            return dependency;
        }

        static JobHandle ProcessObstaclesLayer(this BuildFieldConfig config, in Field field, JobHandle dependency)
        {
            if (!config.HasObstaclesLayer) return dependency;

            var cellsHandle = Physics.BuildCollisionLayer(field.CellColliders).WithSettings(config.ObstaclesLayerSettings).ScheduleParallel(out var cells, Allocator.TempJob, dependency);
            var obstaclesJob = Physics.FindPairs(in config.ObstaclesLayer, in cells, new FlowFieldInternal.ObstaclesProcessor { Field = field }).ScheduleParallel(cellsHandle);
            return cells.Dispose(obstaclesJob);
        }

        #endregion

        #region Validators

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void ValidateSettings(this BuildFieldConfig config)
        {
            if (math.any(config.FieldSettings.FieldSize <= 0))
                throw new InvalidOperationException("BuildField requires a valid field size");
            if (math.any(config.FieldSettings.CellSize <= 0))
                throw new InvalidOperationException("BuildField requires a valid cell size");
        }

        #endregion
    }
}