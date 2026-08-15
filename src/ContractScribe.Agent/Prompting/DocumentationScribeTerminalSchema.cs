using System.Collections.Immutable;

namespace ContractScribe.Agent.Prompting;

internal static class DocumentationScribeTerminalSchema
{
    internal static readonly ImmutableArray<byte> Utf8 = CanonicalJson.Normalize(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": {
            "identifier": {
              "type": "string",
              "minLength": 1,
              "maxLength": 128,
              "pattern": "^[a-z][a-z0-9.-]*[a-z0-9]$|^[a-z]$"
            },
            "symbolRef": {
              "type": "object",
              "additionalProperties": false,
              "required": ["compilationContextRef", "documentationCommentId"],
              "properties": {
                "compilationContextRef": { "type": "string", "minLength": 1, "maxLength": 128 },
                "documentationCommentId": { "type": "string", "minLength": 3, "maxLength": 1024 }
              }
            },
            "span": {
              "type": "object",
              "additionalProperties": false,
              "required": ["start", "end"],
              "properties": {
                "start": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
                "end": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
              }
            },
            "locator": {
              "oneOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["repository"],
                  "properties": {
                    "repository": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path"],
                      "properties": {
                        "path": { "type": "string", "minLength": 1, "maxLength": 1024 },
                        "span": { "$ref": "#/$defs/span" }
                      }
                    }
                  }
                },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["metadata"],
                  "properties": {
                    "metadata": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["assemblyIdentity", "documentationCommentId"],
                      "properties": {
                        "assemblyIdentity": { "type": "string", "minLength": 1, "maxLength": 128 },
                        "documentationCommentId": { "type": "string", "minLength": 3, "maxLength": 1024 }
                      }
                    }
                  }
                },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["generatedOutput"],
                  "properties": {
                    "generatedOutput": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["producerKind", "producerId", "outputId", "sourceSha256"],
                      "properties": {
                        "producerKind": { "enum": ["source-generator", "tool-generated"] },
                        "producerId": { "type": "string", "minLength": 68, "maxLength": 68 },
                        "outputId": { "type": "string", "minLength": 68, "maxLength": 68 },
                        "sourceSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                        "span": { "$ref": "#/$defs/span" }
                      }
                    }
                  }
                },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["synthetic"],
                  "properties": {
                    "synthetic": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["fixtureId"],
                      "properties": {
                        "fixtureId": { "type": "string", "minLength": 1, "maxLength": 128 }
                      }
                    }
                  }
                }
              ]
            },
            "target": {
              "type": "object",
              "additionalProperties": false,
              "required": ["repositoryContextRef", "symbolRef", "sourceCommitment"],
              "properties": {
                "repositoryContextRef": { "type": "string", "pattern": "^repoctx-[0-9a-f]{32}$" },
                "symbolRef": { "$ref": "#/$defs/symbolRef" },
                "sourceCommitment": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["locator", "contentSha256"],
                  "properties": {
                    "locator": { "$ref": "#/$defs/locator" },
                    "contentSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" }
                  }
                }
              }
            },
            "baseUnitProperties": {
              "lines": {
                "type": "array",
                "minItems": 1,
                "maxItems": 128,
                "items": { "type": "string", "minLength": 1, "maxLength": 2048 }
              },
              "claimCategoryId": { "$ref": "#/$defs/identifier" },
              "evidenceReferenceIds": {
                "type": "array",
                "minItems": 1,
                "maxItems": 512,
                "items": { "$ref": "#/$defs/identifier" }
              }
            },
            "plainUnit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "lines", "claimCategoryId", "evidenceReferenceIds"],
              "properties": {
                "kind": { "enum": ["content.summary", "content.remarks"] },
                "lines": { "$ref": "#/$defs/baseUnitProperties/lines" },
                "claimCategoryId": { "$ref": "#/$defs/baseUnitProperties/claimCategoryId" },
                "evidenceReferenceIds": { "$ref": "#/$defs/baseUnitProperties/evidenceReferenceIds" }
              }
            },
            "namedComponentUnit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "componentIdentity", "name", "lines", "claimCategoryId", "evidenceReferenceIds"],
              "properties": {
                "kind": { "enum": ["content.type-parameter", "content.parameter"] },
                "componentIdentity": { "type": "string", "minLength": 1, "maxLength": 128 },
                "name": { "type": "string", "minLength": 1, "maxLength": 128 },
                "lines": { "$ref": "#/$defs/baseUnitProperties/lines" },
                "claimCategoryId": { "$ref": "#/$defs/baseUnitProperties/claimCategoryId" },
                "evidenceReferenceIds": { "$ref": "#/$defs/baseUnitProperties/evidenceReferenceIds" }
              }
            },
            "componentUnit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "componentIdentity", "lines", "claimCategoryId", "evidenceReferenceIds"],
              "properties": {
                "kind": { "enum": ["content.return", "content.value"] },
                "componentIdentity": { "type": "string", "minLength": 1, "maxLength": 128 },
                "lines": { "$ref": "#/$defs/baseUnitProperties/lines" },
                "claimCategoryId": { "$ref": "#/$defs/baseUnitProperties/claimCategoryId" },
                "evidenceReferenceIds": { "$ref": "#/$defs/baseUnitProperties/evidenceReferenceIds" }
              }
            },
            "exceptionUnit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "typeDocumentationId", "lines", "claimCategoryId", "evidenceReferenceIds"],
              "properties": {
                "kind": { "const": "content.exception" },
                "typeDocumentationId": { "type": "string", "minLength": 3, "maxLength": 1024 },
                "lines": { "$ref": "#/$defs/baseUnitProperties/lines" },
                "claimCategoryId": { "$ref": "#/$defs/baseUnitProperties/claimCategoryId" },
                "evidenceReferenceIds": { "$ref": "#/$defs/baseUnitProperties/evidenceReferenceIds" }
              }
            },
            "inheritDocUnit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "lines", "claimCategoryId", "evidenceReferenceIds"],
              "properties": {
                "kind": { "const": "content.inherit-doc" },
                "lines": { "type": "array", "maxItems": 0 },
                "claimCategoryId": { "$ref": "#/$defs/identifier" },
                "evidenceReferenceIds": { "$ref": "#/$defs/baseUnitProperties/evidenceReferenceIds" }
              }
            },
            "contentUnit": {
              "oneOf": [
                { "$ref": "#/$defs/plainUnit" },
                { "$ref": "#/$defs/namedComponentUnit" },
                { "$ref": "#/$defs/componentUnit" },
                { "$ref": "#/$defs/exceptionUnit" },
                { "$ref": "#/$defs/inheritDocUnit" }
              ]
            }
          },
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "target", "contentUnits"],
              "properties": {
                "kind": { "const": "proposal" },
                "target": { "$ref": "#/$defs/target" },
                "contentUnits": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 256,
                  "items": { "$ref": "#/$defs/contentUnit" }
                }
              }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "reason", "evidenceReferenceIds"],
              "properties": {
                "kind": { "const": "skip" },
                "reason": {
                  "enum": [
                    "scribe.skip.insufficient-evidence",
                    "scribe.skip.unsupported-current-m3-domain"
                  ]
                },
                "evidenceReferenceIds": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 512,
                  "items": { "$ref": "#/$defs/identifier" }
                }
              }
            }
          ]
        }
        """u8.ToArray());
}
