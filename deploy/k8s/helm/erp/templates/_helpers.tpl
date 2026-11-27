{{- define "erp.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "erp.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "erp.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
app.kubernetes.io/name: {{ include "erp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: erp
{{- end -}}

{{- define "erp.selectorLabels" -}}
app.kubernetes.io/name: {{ include "erp.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "erp.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "erp.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* image reference: registry/repository[:tag][@digest] */}}
{{- define "erp.image" -}}
{{- $tag := default .root.Chart.AppVersion .image.tag -}}
{{- if .image.digest -}}
{{- printf "%s/%s@%s" .image.registry .image.repository .image.digest -}}
{{- else -}}
{{- printf "%s/%s:%s" .image.registry .image.repository $tag -}}
{{- end -}}
{{- end -}}

{{/* Secret files: /run/secrets/<key> (ASP.NET Core key-per-file configuration). */}}
{{- define "erp.secretVolume" -}}
- name: secrets
  secret:
    secretName: {{ required "secrets.existingSecret is required" .Values.secrets.existingSecret }}
    defaultMode: 0400
- name: tmp
  emptyDir:
    medium: Memory
    sizeLimit: 64Mi
{{- end -}}

{{- define "erp.volumeMounts" -}}
- name: secrets
  mountPath: /run/secrets
  readOnly: true
- name: tmp
  mountPath: /tmp
{{- end -}}

{{- define "erp.probes" -}}
startupProbe:
  httpGet: { path: /health/live, port: http }
  periodSeconds: {{ .Values.probes.startup.periodSeconds }}
  failureThreshold: {{ .Values.probes.startup.failureThreshold }}
  timeoutSeconds: {{ .Values.probes.startup.timeoutSeconds }}
livenessProbe:
  httpGet: { path: /health/live, port: http }
  periodSeconds: {{ .Values.probes.liveness.periodSeconds }}
  failureThreshold: {{ .Values.probes.liveness.failureThreshold }}
  timeoutSeconds: {{ .Values.probes.liveness.timeoutSeconds }}
readinessProbe:
  httpGet: { path: /health/ready, port: http }
  periodSeconds: {{ .Values.probes.readiness.periodSeconds }}
  failureThreshold: {{ .Values.probes.readiness.failureThreshold }}
  timeoutSeconds: {{ .Values.probes.readiness.timeoutSeconds }}
{{- end -}}
