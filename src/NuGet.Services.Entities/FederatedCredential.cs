// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuGet.Services.Entities
{
    /// <summary>
    /// The record of a federated credential that was accepted by a federated credential policy.
    /// </summary>
    public class FederatedCredential : IEntity
    {
        /// <summary>
        /// The unique key for this federated credential. Generated by the database.
        /// </summary>
        public int Key { get; set; }

        /// <summary>
        /// A type enum of the <see cref="FederatedCredentialPolicy.Type"/> that accepted this federated credential.
        /// </summary>
        [Required]
        [Column("TypeKey")]
        public FederatedCredentialType Type { get; set; }

        /// <summary>
        /// The key of the federated credential policy that accepted this federated credential. This does not have a
        /// foreign key constraint because the policy may be deleted, but the credential record should remain to ensure
        /// the <see cref="Identity"/> unique constraint is enforced (to prevent replay).
        /// </summary>
        public int FederatedCredentialPolicyKey { get; set; }

        /// <summary>
        /// A unique identifier for the federated credential used to create this credential record. For OIDC tokens,
        /// this is the "jti" or "uti" claim. This must be unique to ensure tokens are not replayed.
        /// </summary>
        [StringLength(maximumLength: 64)]
        public string Identity { get; set; }

        /// <summary>
        /// When this record was first created. The timestamp is in UTC.
        /// </summary>
        public DateTime Created { get; set; }

        /// <summary>
        /// When the federated credential expires. For OIDC tokens, this will be the "exp" claim. The timestamp is in UTC.
        /// </summary>
        public DateTime Expires { get; set; }
    }
}