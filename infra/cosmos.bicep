param accountName string
param location string
param databaseName string
param containerName string
param backendPrincipalId string = ''
param createAccount bool = false

module provisionAccount 'br/public:avm/res/document-db/database-account:0.21.1' = if (createAccount) {
  params: {
    name: accountName
    location: location
    capacityMode: 'Serverless'
    defaultConsistencyLevel: 'Session'
    zoneRedundant: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    disableLocalAuthentication: true
    disableKeyBasedMetadataWriteAccess: true
    enableTelemetry: false
  }
}

resource account 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: accountName
}
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2025-04-15' = {
  parent: account
  name: databaseName
  location: location
  properties: {
    resource: { id: databaseName }
    options: {}
  }
  dependsOn: [provisionAccount]
}
resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: containerName
  location: location
  properties: {
    resource: {
      id: containerName
      partitionKey: { paths: ['/userId'], kind: 'Hash', version: 2 }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: false
        includedPaths: []
        excludedPaths: [{ path: '/*' }]
      }
    }
    options: {}
  }
}
resource dataAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = if (!empty(backendPrincipalId)) {
  parent: account
  name: guid(account.id, containerName, backendPrincipalId, 'northstar-data')
  properties: {
    principalId: backendPrincipalId
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${account.id}/dbs/${databaseName}/colls/${containerName}'
  }
  dependsOn: [container]
}
output endpoint string = account.properties.documentEndpoint
