targetScope = 'subscription'

@description('Resource group containing the existing Cosmos account.')
param cosmosResourceGroup string = 'seachpoi-rg'
@description('Resource group containing the existing storage account.')
param storageResourceGroup string = 'cloud-shell-storage-westeurope'
param cosmosAccountName string = 'cosmos-agents12'
@description('Explicitly manage a serverless Cosmos account. Leave false for accounts managed elsewhere.')
param createCosmosAccount bool = false
param cosmosLocation string = 'westeurope'
@description('False deploys Cosmos only, leaving existing Blob resources untouched.')
param deployStorage bool = true
param storageAccountName string = 'saaiagentsworkflow'
param databaseName string = 'Northstar'
param profileContainerName string = 'CareerProfiles'
param cvContainerName string = 'northstar-cvs'
@description('Optional backend/development principal object ID. Empty means no Cosmos data role assignment.')
param backendPrincipalId string = ''

resource existingCosmos 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: cosmosAccountName
  scope: resourceGroup(cosmosResourceGroup)
}

module cosmos './cosmos.bicep' = {
  name: 'dev2lead-cosmos'
  scope: resourceGroup(cosmosResourceGroup)
  params: {
    accountName: cosmosAccountName
    location: createCosmosAccount ? cosmosLocation : existingCosmos.location
    createAccount: createCosmosAccount
    databaseName: databaseName
    containerName: profileContainerName
    backendPrincipalId: backendPrincipalId
  }
}

module storage './storage.bicep' = if (deployStorage) {
  name: 'dev2lead-private-cvs'
  scope: resourceGroup(storageResourceGroup)
  params: {
    accountName: storageAccountName
    containerName: cvContainerName
  }
}

output cosmosEndpoint string = cosmos.outputs.endpoint
output blobEndpoint string = deployStorage ? storage!.outputs.endpoint : ''
output database string = databaseName
output profileContainer string = profileContainerName
output cvContainer string = cvContainerName
