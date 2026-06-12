{{/*
Expand the name of the chart.
*/}}
{{- define "aws2azure.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this
(by the DNS naming spec).
*/}}
{{- define "aws2azure.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "aws2azure.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "aws2azure.labels" -}}
helm.sh/chart: {{ include "aws2azure.chart" . }}
{{ include "aws2azure.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "aws2azure.selectorLabels" -}}
app.kubernetes.io/name: {{ include "aws2azure.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
The name of the ServiceAccount to use.
*/}}
{{- define "aws2azure.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "aws2azure.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
The name of the Secret that holds the proxy config.
*/}}
{{- define "aws2azure.configSecretName" -}}
{{- if .Values.config.create }}
{{- printf "%s-config" (include "aws2azure.fullname" .) }}
{{- else }}
{{- required "config.existingSecret is required when config.create=false" .Values.config.existingSecret }}
{{- end }}
{{- end }}

{{/*
The key within the config Secret that holds the JSON file.
*/}}
{{- define "aws2azure.configSecretKey" -}}
{{- if .Values.config.create }}
{{- "config.json" }}
{{- else }}
{{- default "config.json" .Values.config.existingSecretKey }}
{{- end }}
{{- end }}
