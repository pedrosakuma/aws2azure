# secretsmanager

## CreateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Input Tags are accepted in the AWS Key/Value array shape (as sent by the AWS SDK) and mapped to the Key Vault tags map; an existing-name conflict (including Key Vault 409) maps to ResourceExistsException.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_CreateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

## DeleteSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `DELETE https://{vault}.vault.azure.net/secrets/{name}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DeleteSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/delete-secret>

## DescribeSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.4`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Tags are returned as an AWS Key/Value array sourced from the Key Vault secret's tags map.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DescribeSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties>

## GetSecretValue

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret>

## ListSecrets

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets?api-version=7.4`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- SecretList entries emit Tags as an AWS Key/Value array and resolve the secret Name/ARN from the Key Vault item id; a JSON-map tag shape would desync the AWS SDK list unmarshaller.
- Pagination: AWS NextToken carries the Key Vault $skiptoken continuation value (and MaxResults maps to Key Vault maxresults); the proxy always rebuilds its own vault URI from the token rather than following an inbound URL, so NextToken cannot be used as an SSRF vector.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_ListSecrets.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties-versions>

## UpdateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}/versions`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Because Key Vault PUT is an upsert, UpdateSecret first checks existence and returns ResourceNotFoundException for a missing secret to match AWS semantics.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UpdateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

