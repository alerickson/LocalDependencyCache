targetScope = 'subscription'

@description('Azure region for all resources.')
param location string

@description('Resource group to create or update.')
param resourceGroupName string = 'rg-localdependencycache'

@minLength(3)
@maxLength(12)
@description('Short lowercase prefix used in resource names.')
param namePrefix string = 'ldcache'

@description('CRON schedule for the sync timer (six fields, UTC). Each run rebuilds the cache in seconds.')
param timerSchedule string = '0 0 * * * *'

@description('PowerShell Gallery OData Search() endpoint.')
param galleryEndpoint string = 'https://www.powershellgallery.com/api/v2/Search()'

@description('Number of most-downloaded packages to cache (latest stable version of each, plus their dependencies).')
param topPackageCount int = 100

@description('Number of versioned cache artifacts to keep.')
param retainVersions int = 10

@allowed([512, 2048, 4096])
@description('Memory per Function instance.')
param instanceMemoryMB int = 2048

@description('Optional public base URL for cache files (for example a custom domain on Front Door). Defaults to the Front Door endpoint.')
param publicBaseUrl string = ''

param tags object = {
  application: 'LocalDependencyCache'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'localdependencycache-resources'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    timerSchedule: timerSchedule
    galleryEndpoint: galleryEndpoint
    topPackageCount: topPackageCount
    retainVersions: retainVersions
    instanceMemoryMB: instanceMemoryMB
    publicBaseUrl: publicBaseUrl
    tags: tags
  }
}

output resourceGroupName string = rg.name
output functionAppName string = resources.outputs.functionAppName
output cacheStorageAccountName string = resources.outputs.cacheStorageAccountName
output publicBaseUrl string = resources.outputs.publicBaseUrl
output manifestUrl string = resources.outputs.manifestUrl
