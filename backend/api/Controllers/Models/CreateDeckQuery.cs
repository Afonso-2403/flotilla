﻿namespace Api.Controllers.Models
{
    public struct CreateDeckQuery
    {
        public string InstallationCode { get; set; }
        public string PlantCode { get; set; }
        public string Name { get; set; }
    }
}