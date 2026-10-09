param accountName string
param containerName string

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: accountName
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: account
  name: 'default'
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: containerName
  properties: { publicAccess: 'None' }
}
output endpoint string = account.properties.primaryEndpoints.blob
