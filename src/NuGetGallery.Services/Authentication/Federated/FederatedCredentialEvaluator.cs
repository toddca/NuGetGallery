// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NuGet.Services.Entities;

#nullable enable

namespace NuGetGallery.Services.Authentication
{
    public interface IFederatedCredentialEvaluator
    {
        Task<EvaluatedFederatedCredentialPolicies> GetMatchingPolicyAsync(IReadOnlyCollection<FederatedCredentialPolicy> policies, string bearerToken);
    }

    public class FederatedCredentialEvaluator : IFederatedCredentialEvaluator
    {
        private readonly IEntraIdTokenValidator _entraIdTokenValidator;
        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly ILogger<FederatedCredentialEvaluator> _logger;

        public FederatedCredentialEvaluator(
            IEntraIdTokenValidator entraIdTokenValidator,
            IDateTimeProvider dateTimeProvider,
            ILogger<FederatedCredentialEvaluator> logger)
        {
            _entraIdTokenValidator = entraIdTokenValidator ?? throw new ArgumentNullException(nameof(entraIdTokenValidator));
            _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<EvaluatedFederatedCredentialPolicies> GetMatchingPolicyAsync(IReadOnlyCollection<FederatedCredentialPolicy> policies, string bearerToken)
        {
            // perform basic validations not specific to any federated credential policy
            // the error message is user-facing and should not leak sensitive information
            var (userError, validatedJwt) = await ValidateJwtByIssuer(bearerToken);
            if (userError is not null)
            {
                return EvaluatedFederatedCredentialPolicies.BadToken(userError);
            }

            if (validatedJwt is null)
            {
                throw new InvalidOperationException("The validated JWT must be set.");
            }

            var results = new List<FederatedCredentialPolicyResult>();

            // sort the policy results by creation date, so older policy results are preferred
            foreach (var policy in policies.OrderBy(x => x.Created))
            {
                var result = EvaluatePolicy(policy, validatedJwt);

                results.Add(result);

                if (result.Type == FederatedCredentialPolicyResultType.Success)
                {
                    return EvaluatedFederatedCredentialPolicies.NewMatchedPolicy(results, result.Policy, result.FederatedCredential);
                }
            }

            return EvaluatedFederatedCredentialPolicies.NoMatchingPolicy(results);
        }

        private FederatedCredentialPolicyResult EvaluatePolicy(FederatedCredentialPolicy policy, ValidatedJwt validatedJwt)
        {
            // Evaluate the policy and return an unauthorized result if the policy does not match.
            // The reason is not shared with the caller to prevent leaking sensitive information.
            string? unauthorizedReason = (validatedJwt.RecognizedIssuer, policy.Type) switch
            {
                (RecognizedIssuer.EntraId, FederatedCredentialType.EntraIdServicePrincipal) => EvaluateEntraIdServicePrincipal(policy, validatedJwt.Jwt),
                _ => "The policy type does not match the token issuer.",
            };

            FederatedCredentialPolicyResult result;
            if (unauthorizedReason is not null)
            {
                result = FederatedCredentialPolicyResult.Unauthorized(policy, unauthorizedReason);
            }
            else
            {
                result = FederatedCredentialPolicyResult.Success(policy, new FederatedCredential
                {
                    Identity = validatedJwt.Identifier,
                    FederatedCredentialPolicyKey = policy.Key,
                    Type = policy.Type,
                    Created = _dateTimeProvider.UtcNow,
                    Expires = validatedJwt.Jwt.ValidTo.ToUniversalTime(),
                });
            }

            _logger.LogInformation(
                "Evaluated policy key {PolicyKey} of type {PolicyType}. Result type: {ResultType}. Reason: {Reason}",
                result.Policy.Key,
                result.Policy.Type,
                result.Type,
                unauthorizedReason ?? "policy matched token");

            return result;
        }

        private enum RecognizedIssuer
        {
            None,
            EntraId,
        }

        private class ValidatedJwt
        {
            public ValidatedJwt(JsonWebToken jwt, string identifier, RecognizedIssuer recognizedIssuer)
            {
                Jwt = jwt;
                Identifier = identifier;
                RecognizedIssuer = recognizedIssuer;
            }

            public JsonWebToken Jwt { get; }

            /// <summary>
            /// This should be the unique token identifier (uti for Entra ID or jti otherwise).
            /// Used to prevent replay and persisted on the <see cref="FederatedCredential"/> entity.
            /// </summary>
            public string Identifier { get; }

            public RecognizedIssuer RecognizedIssuer { get; }
        }

        /// <summary>
        /// Parse the bearer token as a JWT, perform basic validation, and find the applicable that apply to all issuers.
        /// </summary>
        private async Task<(string? UserError, ValidatedJwt?)> ValidateJwtByIssuer(string bearerToken)
        {
            JsonWebToken jwt;
            try
            {
                jwt = new JsonWebToken(bearerToken);
            }
            catch (ArgumentException)
            {
                return ("The bearer token could not be parsed as a JSON web token.", null);
            }

            if (jwt.Audiences.Count() != 1)
            {
                return ("The JSON web token must have exactly one aud claim value.", null);
            }

            if (string.IsNullOrWhiteSpace(jwt.Audiences.Single()))
            {
                return ("The JSON web token must have an aud claim.", null);
            }

            if (string.IsNullOrWhiteSpace(jwt.Issuer))
            {
                return ("The JSON web token must have an iss claim.", null);      
            }

            if (!Uri.TryCreate(jwt.Issuer, UriKind.Absolute, out var issuerUrl)
                || issuerUrl.Scheme != "https")
            {
                return ("The JSON web token iss claim must be a valid HTTPS URL.", null);
            }

            RecognizedIssuer issuer;
            string? userError;
            string? identifier;
            TokenValidationResult? validationResult;
            switch (issuerUrl.Authority)
            {
                case "login.microsoftonline.com":
                    issuer = RecognizedIssuer.EntraId;
                    (userError, identifier, validationResult) = await GetEntraIdValidationResultAsync(jwt);
                    break;
                default:
                    return ("The JSON web token iss claim is not supported.", null);
            }

            if (userError is not null)
            {
                return (userError, null);
            }

            if (string.IsNullOrWhiteSpace(identifier) || validationResult is null)
            {
                throw new InvalidOperationException("The identifier and validation result must be set.");
            }

            if (validationResult.IsValid)
            {
                return (null, new ValidatedJwt(jwt, identifier!, issuer));
            }

            var validationException = validationResult.Exception;

            userError = validationException switch
            {
                SecurityTokenExpiredException => "The JSON web token has expired.",
                SecurityTokenInvalidAudienceException => "The JSON web token has an incorrect audience.",
                SecurityTokenInvalidSignatureException => "The JSON web token has an invalid signature.",
                _ => "The JSON web token could not be validated.",
            };

            _logger.LogInformation(validationException, "The JSON web token with recognized issuer {Issuer} could not be validated.", issuer);

            return (userError, null);
        }
        
        private async Task<(string? UserError, string? Identifier, TokenValidationResult? Result)> GetEntraIdValidationResultAsync(JsonWebToken jwt)
        {
            const string UniqueTokenIdentifierClaim = "uti"; // unique token identifier (equivalent to jti)

            if (!jwt.TryGetPayloadValue<string>(UniqueTokenIdentifierClaim, out var uti)
                || string.IsNullOrWhiteSpace(uti))
            {
                return ($"The JSON web token must have a {UniqueTokenIdentifierClaim} claim.", null, null);
            }

            var tokenValidationResult = await _entraIdTokenValidator.ValidateAsync(jwt);
            return (null, uti, tokenValidationResult);
        }

        private string? EvaluateEntraIdServicePrincipal(FederatedCredentialPolicy policy, JsonWebToken jwt)
        {
            // See https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference
            const string ClientCredentialTypeClaim = "azpacr";
            const string ClientCertificateType = "2"; // 2 indicates a client certificate (or managed identity) was used
            const string IdentityTypeClaim = "idtyp";
            const string AppIdentityType = "app";
            const string VersionClaim = "ver";
            const string Version2 = "2.0";

            if (!jwt.TryGetPayloadValue<string>(ClaimConstants.Tid, out var tid))
            {
                return $"The JSON web token is missing the {ClaimConstants.Tid} claim.";
            }

            if (!jwt.TryGetPayloadValue<string>(ClaimConstants.Oid, out var oid))
            {
                return $"The JSON web token is missing the {ClaimConstants.Oid} claim.";
            }

            if (!jwt.TryGetPayloadValue<string>(ClientCredentialTypeClaim, out var azpacr))
            {
                return $"The JSON web token is missing the {ClientCredentialTypeClaim} claim.";
            }

            if (azpacr != ClientCertificateType)
            {
                return $"The JSON web token must have an {ClientCredentialTypeClaim} claim with a value of {ClientCertificateType}.";
            }

            if (!jwt.TryGetPayloadValue<string>(IdentityTypeClaim, out var idtyp))
            {
                return $"The JSON web token is missing the {IdentityTypeClaim} claim.";
            }

            if (idtyp != AppIdentityType)
            {
                return $"The JSON web token must have an {IdentityTypeClaim} claim with a value of {AppIdentityType}.";
            }

            if (!jwt.TryGetPayloadValue<string>(VersionClaim, out var ver))
            {
                return $"The JSON web token is missing the {VersionClaim} claim.";
            }

            if (ver != Version2)
            {
                return $"The JSON web token must have a {VersionClaim} claim with a value of {Version2}.";
            }

            if (jwt.Subject != oid)
            {
                return $"The JSON web token {ClaimConstants.Sub} claim must match the {ClaimConstants.Oid} claim.";
            }

            var criteria = DeserializePolicy<EntraIdServicePrincipalCriteria>(policy);

            if (string.IsNullOrWhiteSpace(tid) || !Guid.TryParse(tid, out var parsedTid) || parsedTid != criteria.TenantId)
            {
                return $"The JSON web token must have a {ClaimConstants.Tid} claim that matches the policy.";
            }

            if (!_entraIdTokenValidator.IsTenantAllowed(parsedTid))
            {
                return "The tenant ID in the JSON web token is not in allow list.";
            }

            if (string.IsNullOrWhiteSpace(oid) || !Guid.TryParse(oid, out var parsedOid) || parsedOid != criteria.ObjectId)
            {
                return $"The JSON web token must have a {ClaimConstants.Oid} claim that matches the policy.";
            }

            return null;
        }

        private static T DeserializePolicy<T>(FederatedCredentialPolicy policy)
        {
            var criteria = JsonSerializer.Deserialize<T>(policy.Criteria);
            if (criteria is null)
            {
                throw new InvalidOperationException("The policy criteria must be a valid JSON object.");
            }

            return criteria;
        }
    }
}
