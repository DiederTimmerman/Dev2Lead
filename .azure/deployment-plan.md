# Dev2Lead Cosmos recreation

Status: Validation blocked - interactive Azure MFA required

## Approved context

- Mode: MODIFY existing MAUI desktop / ASP.NET Core backend application.
- Recipe: standalone Bicep, using the existing deployment wrapper.
- Subscription: Visual Studio Enterprise - MPN (`c81a5703-6b68-42ee-8011-30eb43cb6748`).
- Tenant: `0071de0e-b827-493b-a8d9-6db970b9cd15`.
- Resource group: `seachpoi-rg`, confirmed existing.
- Location: `westeurope`, explicitly approved.
- Cosmos account: `cosmos-agents12`, ARM lookup confirmed ResourceNotFound.
- Serverless NoSQL database: `Northstar`; container: `CareerProfiles`; partition: `/userId`.
- Original CV storage remains the existing private Blob storage configuration.

## Scope and safeguards

Create the missing account, database and container using an explicit account-creation mode.
Preserve existing-account mode for backwards compatibility. Do not delete or reset resources.
Support a Cosmos-only deployment so unrelated Blob resources need not be changed.
Keep API credentials out of the desktop, templates, outputs and logs.
Prefer Azure CLI identity and container-scoped data access for local API development.
The previous key cannot be assumed valid for a recreated account.

## Steps

- [x] Inspect infrastructure and confirm missing account through ARM.
- [x] Confirm subscription, resource group, region and permission to record this plan.
- [x] Research resource schema, security and region support.
- [x] Update Bicep account-creation mode and deployment wrapper.
- [x] Compile Bicep and validate wrapper/parameters.
- [ ] Run Azure validation and preview deployment.
- [ ] Obtain explicit deployment approval.
- [ ] Deploy and verify Cosmos account/database/container.
- [ ] Update API-only configuration and verify account persistence without altering consent.

## Risks

Global name availability, serverless regional capacity, subscription policy, MFA and RBAC
may block deployment. Existing data from a deleted account is not restored by creating new
resources. No destructive reset or recovery of deleted user data is authorized.

## Research evidence

ARM lists zero Cosmos accounts in this subscription; the requested name is globally available.
The provider advertises West Europe. Cosmos is unsupported by Microsoft.Quota; use its published
service limits and ARM validation rather than interpreting missing quota records as unlimited capacity.
Account provisioning uses AVM `avm/res/document-db/database-account:0.21.1`, with Entra-only
authentication and serverless capacity. Optional data access remains scoped to the container.

## All validation checks pass

- [ ] Core validation: CLI, authentication, Bicep build, ARM validate and what-if.
- [x] Azure Policy validation: no policy assignments returned for this subscription.
- [x] Static role verification for the local development identity.

## Validation tooling

The official workflow uses an Enum.TryParse overload unavailable in Windows PowerShell 5.
A session-only copy substitutes Enum.Parse after checking the allowed values. Workflow steps
and progress recording are otherwise unchanged; the installed skill is not modified.

## 7. Validation Proof

- Bicep compilation passed without diagnostics; the PowerShell wrapper parses successfully.
- Azure CLI installed and authenticated to the approved subscription.
- ARM validation and what-if both failed with `RequestDisallowedByAzure` / `AADSTS50076`:
  interactive MFA is required for resource management. No deployment was attempted.
- Policy assignment listing returned an empty list; this does not bypass mandatory MFA.
- Reviewed Cosmos Built-in Data Contributor role `00000000-0000-0000-0000-000000000002`,
  scoped to `/dbs/Northstar/colls/CareerProfiles` for the current development principal.
- Exact Cosmos-only parameters are in `infra/cosmos-recreate.parameters.json`.
- The plan must not be marked Validated until ARM validation and preview pass after MFA.
