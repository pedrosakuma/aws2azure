# secretsmanager

## CreateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.

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

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UpdateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

