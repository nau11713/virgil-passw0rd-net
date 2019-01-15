﻿/*
 * Copyright (C) 2015-2018 Virgil Security Inc.
 *
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are
 * met:
 *
 *     (1) Redistributions of source code must retain the above copyright
 *     notice, this list of conditions and the following disclaimer.
 *
 *     (2) Redistributions in binary form must reproduce the above copyright
 *     notice, this list of conditions and the following disclaimer in
 *     the documentation and/or other materials provided with the
 *     distribution.
 *
 *     (3) Neither the name of the copyright holder nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE AUTHOR ''AS IS'' AND ANY EXPRESS OR
 * IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
 * IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 *
 * Lead Maintainer: Virgil Security Inc. <support@virgilsecurity.com>
 */

namespace Passw0rd
{
    using System;
    using System.Threading.Tasks;

    using Passw0rd.Phe;
    using Passw0rd.Client;
    using Passw0rd.Utils;
    using System.Linq;
    using Passw0Rd;
    using global::Phe;
    using Google.Protobuf;

    // using global::Phe;

    /// <summary>
    /// The <see cref="Protocol"/> provides an implementation of PHE (Password 
    /// Hardened Encryption) scheme.
    /// </summary>
    /// <remarks>
    /// With the help of an external crypto server, a service provider can recover 
    /// the user data encrypted by PHE only when an end user supplied a correct password. 
    /// PHE inherits the security features of password-hardening, adding protection
    /// for the user data. In particular, the crypto server does not learn any 
    /// information about any user data. More importantly, both the crypto server 
    /// and the service provider can rotate their secret keys, a proactive security 
    /// mechanism mandated by the Payment Card Industry Data Security Standard (PCI DSS).
    /// </remarks>
    public class Protocol
    {
        private readonly ProtocolContext ctx;

        /// <summary>
        /// Initializes a new instance of the <see cref="Protocol"/> class.
        /// </summary>
        public Protocol(ProtocolContext context)
        {
            this.ctx = context;
        }

        /// <summary>
        /// Enrolls a new <see cref="DatabaseRecord"/> for specified password.
        /// </summary>
        public async Task<byte[]> EnrollAsync(string password)
        {
            var version = this.ctx.Version;
            var skC = this.ctx.ClientSecretKey;
            var enrollmentResp = await this.ctx.Client.GetEnrollment(
                new EnrollmentRequest() { Version = version })
                .ConfigureAwait(false);
            var pheResp = global::Phe.EnrollmentResponse.Parser.ParseFrom(enrollmentResp.Response);
            var isValid = this.ctx.Crypto.ValidateProofOfSuccess(
                pheResp.Proof,
                this.ctx.ServerPublicKey,
                pheResp.Ns.ToByteArray(),
                pheResp.C0.ToByteArray(),
                pheResp.C1.ToByteArray());
            if (!isValid)
            {
                throw new ProofOfSuccessNotValidException();
            }

            var nS = pheResp.Ns;
            var nC = this.ctx.Crypto.GenerateNonce();
            var pwdBytes = Bytes.FromString(password);

            var (t0, t1) = ctx.Crypto.ComputeT(skC, pwdBytes, nC, pheResp.C0.ToByteArray(),pheResp.C1.ToByteArray());

            var enrollmentRecord = new EnrollmentRecord
            {
                Nc = ByteString.CopyFrom(nC),
                Ns = nS,
                T0 = ByteString.CopyFrom(t0),
                T1 = ByteString.CopyFrom(t1)
            };

            var record = new DatabaseRecord
            {
                Version = version,
                Record = ByteString.CopyFrom(enrollmentRecord.ToByteArray())
                                   
            };
            return enrollmentRecord.ToByteArray();
        }

        /// <summary>
        /// Verifies a <see cref="DatabaseRecord"/> by specified password.
        /// </summary>
        public async Task<byte[]> VerifyAsync(byte[] pwdRecord, string password)
        {
            var pwdBytes = Bytes.FromString(password);
            var databaseRecord = DatabaseRecord.Parser.ParseFrom(pwdRecord);
            if (databaseRecord.Version != ctx.Version){
                //todo more specific exception???
                throw new Exception("unable to find keys corresponding to this record's version");
            }
            var enrollmentRecord = EnrollmentRecord.Parser.ParseFrom(databaseRecord.Record);
            // todo validate len(enrollmentRecord.Nc) len(enrollmentRecord.Ns) = 32 pheNonceLen
            var c0 = this.ctx.Crypto.ComputeC0(
                this.ctx.ClientSecretKey, pwdBytes, enrollmentRecord.Nc.ToByteArray(), enrollmentRecord.T0.ToByteArray());
            
            var pheVerifyPasswordRequest = new global::Phe.VerifyPasswordRequest()
            {
                Ns = enrollmentRecord.Ns,
                C0 = ByteString.CopyFrom(c0)
            };

            var versionedPasswordRequest = new Passw0Rd.VerifyPasswordRequest()
            {
                Version = ctx.Version,
                Request = ByteString.CopyFrom(pheVerifyPasswordRequest.ToByteArray())
            };

            //todo VerifyAsync(VerifyPasswordRequest)
            var serverResponse = await this.ctx.Client.VerifyAsync(versionedPasswordRequest).ConfigureAwait(false);
            // todo if response == null exception 
          

            byte[] m = null;
            var pheServerResponse = global::Phe.VerifyPasswordResponse.Parser.ParseFrom(serverResponse.Response);

            if (pheServerResponse.Res)
            {
                if (pheServerResponse.Success == null)
                {
                    throw new ProofNotProvidedException();
                }

           
                var isValid = this.ctx.Crypto.ValidateProofOfSuccess(pheServerResponse.Success, 
                                                                     ctx.ServerPublicKey,
                                                                     enrollmentRecord.Ns.ToByteArray(),
                                                                     c0, pheServerResponse.C1.ToByteArray());
                if (!isValid)
                {
                    throw new ProofOfSuccessNotValidException();
                }

                m = this.ctx.Crypto.DecryptM(ctx.ClientSecretKey, 
                                             pwdBytes,
                                             enrollmentRecord.Nc.ToByteArray(), 
                                             enrollmentRecord.T1.ToByteArray(), 
                                             pheServerResponse.C1.ToByteArray());
            }
            else
            {
                if (pheServerResponse.Fail == null)
                {
                    throw new ProofNotProvidedException();
                }

                var isValid = this.ctx.Crypto.ValidateProofOfFail(pheServerResponse.Fail,
                                                                  ctx.ServerPublicKey,
                                                                  enrollmentRecord.Ns.ToByteArray(),
                                                                  c0, pheServerResponse.C1.ToByteArray());

                if (!isValid)
                {
                    throw new ProofOfFailNotValidException();
                }
            }
            return m;
        }

        /// <summary>
        /// Updates a <see cref="DatabaseRecord"/> with an specified <see cref="VersionedUpdateToken"/>.
        /// </summary>
        public byte[] Update(byte[] pwdRecord)
        {
            if (pwdRecord == null)
            {
                throw new ArgumentNullException(nameof(pwdRecord));
            }

            if (this.ctx.UpdateToken == null)
            {
                throw new Passw0rdProtocolException("UpdateToken is not provided in context");
            }
            var databaseRecord = DatabaseRecord.Parser.ParseFrom(pwdRecord);

            if (databaseRecord.Version == ctx.UpdateToken.Version){
                return pwdRecord;
            }

            if (databaseRecord.Version + 1 == ctx.UpdateToken.Version){
                var enrollmentRecord = EnrollmentRecord.Parser.ParseFrom(databaseRecord.Record);

                var t0 = enrollmentRecord.T0.ToByteArray();
                var t1 = enrollmentRecord.T1.ToByteArray();
                (t0, t1) = ctx.Crypto.UpdateT(enrollmentRecord.Ns.ToByteArray(),
                                              enrollmentRecord.T0.ToByteArray(),
                                              enrollmentRecord.T1.ToByteArray(),
                                              ctx.UpdateToken.ToByteArray());


                var updatedEnrollmentRecord = new EnrollmentRecord
                {
                    Nc = enrollmentRecord.Nc,
                    Ns = enrollmentRecord.Ns,
                    T0 = ByteString.CopyFrom(t0),
                    T1 = ByteString.CopyFrom(t1)
                };

                var updatedDatabaseRecord = new DatabaseRecord
                {
                    Version = ctx.UpdateToken.Version,
                    Record = ByteString.CopyFrom(enrollmentRecord.ToByteArray())

                };
                return updatedDatabaseRecord.ToByteArray();
            }

            throw new Passw0rdProtocolException(
                String.Format("Record and update token versions mismatch: {0} and {1}",
                              databaseRecord.Version, ctx.UpdateToken.Version)
            );
        }
    }
}
