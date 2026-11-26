param location string
param namePrefix string
param timerSchedule string
param galleryEndpoint string
param topPackageCount int
param retainVersions int
param instanceMemoryMB int
param publicBaseUrl string
param tags object

var token = uniqueString(resourceGroup().id)
var storageSuffix = environment().suffixes.storage
var cacheContainerName = 'cache'
var deploymentContainerName = 'app-package'
var functionAppName = '${namePrefix}-func-${token}'

var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

// ---------------------------------------------------------------------------
// Monitoring
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-log-${token}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    DisableLocalAuth: true
  }
}

// ---------------------------------------------------------------------------
// Function host storage (private): runtime state, timer locks, deployment package
// ---------------------------------------------------------------------------

resource functionStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${namePrefix}fn${token}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
  }

  resource blobService 'blobServices' = {
    name: 'default'

    resource deploymentContainer 'containers' = {
      name: deploymentContainerName
    }
  }
}

// ---------------------------------------------------------------------------
// Cache storage (public read): published artifacts served through Front Door.
// Anonymous read is limited to blobs in the cache container; listing is not allowed.
// ---------------------------------------------------------------------------

resource cacheStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${namePrefix}cache${token}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: true
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
  }

  resource blobService 'blobServices' = {
    name: 'default'

    resource cacheContainer 'containers' = {
      name: cacheContainerName
      properties: {
        publicAccess: 'Blob'
      }
    }
  }
}

var cacheBlobHost = '${cacheStorage.name}.blob.${storageSuffix}'

// ---------------------------------------------------------------------------
// Front Door Standard (CDN) in front of the cache container
// ---------------------------------------------------------------------------

resource frontDoor 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: '${namePrefix}-afd-${token}'
  location: 'global'
  tags: tags
  sku: { name: 'Standard_AzureFrontDoor' }
}

resource frontDoorEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: frontDoor
  name: '${namePrefix}-${token}'
  location: 'global'
  tags: tags
  properties: {
    enabledState: 'Enabled'
  }
}

// A single origin, so health probes are left off.
resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'cache-storage'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    sessionAffinityState: 'Disabled'
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'cache-blob'
  properties: {
    hostName: cacheBlobHost
    originHostHeader: cacheBlobHost
    httpPort: 80
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

// Front Door honors the origin Cache-Control headers set by the publisher:
// immutable for versions/, five minutes for latest/.
resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: frontDoorEndpoint
  name: 'cache'
  dependsOn: [
    origin
  ]
  properties: {
    originGroup: { id: originGroup.id }
    patternsToMatch: [
      '/${cacheContainerName}/*'
    ]
    supportedProtocols: [
      'Http'
      'Https'
    ]
    httpsRedirect: 'Enabled'
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    cacheConfiguration: {
      queryStringCachingBehavior: 'IgnoreQueryString'
      compressionSettings: {
        isCompressionEnabled: false
      }
    }
  }
}

var effectiveBaseUrl = empty(publicBaseUrl)
  ? 'https://${frontDoorEndpoint.properties.hostName}/${cacheContainerName}'
  : publicBaseUrl

// ---------------------------------------------------------------------------
// Function App (Flex Consumption, system-assigned identity)
// ---------------------------------------------------------------------------

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-plan-${token}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${functionStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        // The timer trigger runs on one instance; this is the plan minimum.
        maximumInstanceCount: 40
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
  }

  resource appSettings 'config' = {
    name: 'appsettings'
    properties: {
      AzureWebJobsStorage__credential: 'managedidentity'
      AzureWebJobsStorage__blobServiceUri: 'https://${functionStorage.name}.blob.${storageSuffix}'
      AzureWebJobsStorage__queueServiceUri: 'https://${functionStorage.name}.queue.${storageSuffix}'
      AzureWebJobsStorage__tableServiceUri: 'https://${functionStorage.name}.table.${storageSuffix}'
      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
      TimerSchedule: timerSchedule
      PackageCache__GalleryEndpoint: galleryEndpoint
      PackageCache__PageSize: '100'
      PackageCache__TopPackageCount: string(topPackageCount)
      PackageCache__IncludeDependencies: 'true'
      PackageCache__RetainVersions: string(retainVersions)
      PackageCache__StorageMode: 'Blob'
      // The deployed package is read-only; work in the instance's temp storage.
      PackageCache__OutputDirectory: '/tmp/localdependencycache'
      PackageCache__BlobContainerUrl: 'https://${cacheBlobHost}/${cacheContainerName}'
      PackageCache__BlobBaseUrl: effectiveBaseUrl
    }
  }
}

// ---------------------------------------------------------------------------
// Role assignments for the Function App identity
// ---------------------------------------------------------------------------

var functionStorageRoles = [
  roles.storageBlobDataOwner
  roles.storageQueueDataContributor
  roles.storageTableDataContributor
]

resource functionStorageRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in functionStorageRoles: {
    name: guid(functionStorage.id, functionApp.id, roleId)
    scope: functionStorage
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
      principalId: functionApp.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Publish artifacts, manage the sync lease, and prune old versions.
resource cacheStorageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cacheStorage.id, functionApp.id, roles.storageBlobDataContributor)
  scope: cacheStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appInsightsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, functionApp.id, roles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringMetricsPublisher)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output cacheStorageAccountName string = cacheStorage.name
output publicBaseUrl string = effectiveBaseUrl
output manifestUrl string = '${effectiveBaseUrl}/latest/psgallery-cache.manifest.json'
