# s3 / GetBucketNotificationConfiguration {#operation-s3-getbucketnotificationconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketnotificationconfiguration`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns an empty <NotificationConfiguration/> document)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketnotificationconfiguration:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Empty body means 'no notifications configured', matching S3. Azure Event Grid / Event Hubs subscriptions are configured out of band.

## Behaviour differences

- GET returns 200 with an empty <NotificationConfiguration/> document, matching the S3 'never configured' wire shape.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketNotificationConfiguration.html>

