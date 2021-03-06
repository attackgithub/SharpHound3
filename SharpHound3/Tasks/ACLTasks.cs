﻿using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using SharpHound3.Enums;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;

namespace SharpHound3.Tasks
{
    internal static class ACLTasks
    {
        private static readonly Dictionary<Type, string> BaseGuids;
        private const string AllGuid = "00000000-0000-0000-0000-000000000000";

        static ACLTasks()
        {
            BaseGuids = new Dictionary<Type, string>
            {
                {typeof(User), "bf967aba-0de6-11d0-a285-00aa003049e2"},
                {typeof(Computer), "bf967a86-0de6-11d0-a285-00aa003049e2"},
                {typeof(Group), "bf967a9c-0de6-11d0-a285-00aa003049e2"},
                {typeof(Domain), "19195a5a-6da0-11d0-afd3-00c04fd930c9"},
                {typeof(GPO), "f30e3bc2-9ff0-11d1-b603-0000f80367c1"},
                {typeof(OU), "bf967aa5-0de6-11d0-a285-00aa003049e2"}
            };
        }

        internal static async Task<LdapWrapper> ProcessDACL(LdapWrapper wrapper)
        {
            var aces = new List<ACL>();
            var ntSecurityDescriptor = wrapper.SearchResult.GetPropertyAsBytes("ntsecuritydescriptor");

            //If the NTSecurityDescriptor is null, something screwy is happening. Nothing to process here, so continue in the pipeline
            if (ntSecurityDescriptor == null)
                return wrapper;

            var descriptor = new ActiveDirectorySecurity();
            descriptor.SetSecurityDescriptorBinaryForm(ntSecurityDescriptor);

            var ownerSid = ProcessACESID(descriptor.GetOwner(typeof(SecurityIdentifier)).Value);
            if (ownerSid != null)
            {
                if (CommonPrincipal.GetCommonSid(ownerSid, out var commonPrincipal))
                {
                    aces.Add(new ACL
                    {
                        PrincipalSID = Helpers.ConvertCommonSid(ownerSid, wrapper.Domain),
                        RightName = "Owner",
                        AceType = "",
                        PrincipalType = commonPrincipal.Type,
                        IsInherited = false
                    });
                }
                else
                {
                    var ownerType = await Helpers.LookupSidType(ownerSid);
                    aces.Add(new ACL
                    {
                        PrincipalSID = ownerSid,
                        RightName = "Owner",
                        AceType = "",
                        PrincipalType = ownerType,
                        IsInherited = false
                    });
                }
            }

            foreach (ActiveDirectoryAccessRule ace in descriptor.GetAccessRules(true,
                true, typeof(SecurityIdentifier)))
            {
                //Ignore Null Aces
                if (ace == null)
                    continue;
                //Ignore deny aces
                if (ace.AccessControlType == AccessControlType.Deny)
                    continue;

                //Check if the ACE actually applies to our object based on the object type
                if (!IsAceInherited(ace, BaseGuids[wrapper.GetType()]))
                    continue;

                var principalSid = ProcessACESID(ace.IdentityReference.Value);

                if (principalSid == null)
                    continue;

                LdapTypeEnum principalType;
                if (CommonPrincipal.GetCommonSid(principalSid, out var commonPrincipal))
                {
                    principalSid = Helpers.ConvertCommonSid(principalSid, wrapper.Domain);
                    principalType = commonPrincipal.Type;
                }
                else
                {
                    principalType = await Helpers.LookupSidType(principalSid);
                }

                var rights = ace.ActiveDirectoryRights;
                var objectAceType = ace.ObjectType.ToString();
                var isInherited = ace.IsInherited;

                if (rights.HasFlag(ActiveDirectoryRights.GenericAll))
                {
                    if (objectAceType == AllGuid || objectAceType == "")
                    {
                        aces.Add(new ACL
                        {
                            PrincipalSID = principalSid,
                            RightName = "GenericAll",
                            AceType = "",
                            PrincipalType = principalType,
                            IsInherited = isInherited
                        });
                    }
                    //GenericAll includes every other right, and we dont want to duplicate. So continue in the loop
                    continue;
                }

                //WriteDacl and WriteOwner are always useful to us regardless of object type
                if (rights.HasFlag(ActiveDirectoryRights.WriteDacl))
                {
                    aces.Add(new ACL
                    {
                        PrincipalSID = principalSid,
                        AceType = "",
                        RightName = "WriteDacl",
                        PrincipalType = principalType,
                        IsInherited = isInherited
                    });
                }

                if (rights.HasFlag(ActiveDirectoryRights.WriteOwner))
                {
                    aces.Add(new ACL
                    {
                        RightName = "WriteOwner",
                        AceType = "",
                        PrincipalSID = principalSid,
                        PrincipalType = principalType,
                        IsInherited = isInherited
                    });
                }

                //Process object specific ACEs
                //Extended rights apply to Users, Domains, Computers
                if (rights.HasFlag(ActiveDirectoryRights.ExtendedRight))
                {
                    if (wrapper is Domain)
                    {
                        switch (objectAceType)
                        {
                            case "1131f6aa-9c07-11d1-f79f-00c04fc2dcd2":
                                aces.Add(new ACL
                                {
                                    AceType = "GetChanges",
                                    RightName = "ExtendedRight",
                                    PrincipalSID = principalSid,
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                                break;
                            case "1131f6ad-9c07-11d1-f79f-00c04fc2dcd2":
                                aces.Add(new ACL
                                {
                                    AceType = "GetChangesAll",
                                    RightName = "ExtendedRight",
                                    PrincipalSID = principalSid,
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                                break;
                            case AllGuid:
                            case "":
                                aces.Add(new ACL
                                {
                                    AceType = "All",
                                    RightName = "ExtendedRight",
                                    PrincipalSID = principalSid,
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                                break;
                        }
                    }else if (wrapper is User)
                    {
                        switch (objectAceType)
                        {
                            case "00299570-246d-11d0-a768-00aa006e0529":
                                aces.Add(new ACL
                                {
                                    AceType = "User-Force-Change-Password",
                                    PrincipalSID = principalSid,
                                    RightName = "ExtendedRight",
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                                break;
                            case AllGuid:
                            case "":
                                aces.Add(new ACL
                                {
                                    AceType = "All",
                                    PrincipalSID = principalSid,
                                    RightName = "ExtendedRight",
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                                break;
                        }
                    }else if (wrapper is Computer)
                    {
                        Helpers.GetDirectorySearcher(wrapper.Domain).GetNameFromGuid(objectAceType, out var mappedGuid);
                        if (wrapper.SearchResult.GetProperty("ms-mcs-admpwdexpirationtime") != null)
                        {
                            if (objectAceType == AllGuid || objectAceType == "")
                            {
                                aces.Add(new ACL
                                {
                                    AceType = "All",
                                    RightName = "ExtendedRight",
                                    PrincipalSID = principalSid,
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                            }else if (mappedGuid != null && mappedGuid == "ms-Mcs-AdmPwd")
                            {
                                aces.Add(new ACL
                                {
                                    AceType = "",
                                    RightName = "ReadLAPSPassword",
                                    PrincipalSID = principalSid,
                                    PrincipalType = principalType,
                                    IsInherited = isInherited
                                });
                            }
                        }
                    }
                }

                //PropertyWrites apply to Groups, User, Computer
                //GenericWrite encapsulates WriteProperty, so we need to check them at the same time to avoid duplicate edges
                if (rights.HasFlag(ActiveDirectoryRights.GenericWrite) ||
                    rights.HasFlag(ActiveDirectoryRights.WriteProperty))
                {
                    if (objectAceType == AllGuid || objectAceType == "")
                    {
                        aces.Add(new ACL
                        {
                            AceType = "",
                            RightName = "GenericWrite",
                            PrincipalSID = principalSid,
                            PrincipalType = principalType,
                            IsInherited = isInherited
                        });
                    }

                    if (wrapper is User)
                    {
                        if (objectAceType == "f3a64788-5306-11d1-a9c5-0000f80367c1")
                        {
                            aces.Add(new ACL
                            {
                                AceType = "WriteSPN",
                                RightName = "WriteProperty",
                                PrincipalSID = principalSid,
                                PrincipalType = principalType,
                                IsInherited = isInherited
                            });
                        }
                    }else if (wrapper is Group)
                    {
                        if (objectAceType == "bf9679c0-0de6-11d0-a285-00aa003049e2")
                        {
                            aces.Add(new ACL
                            {
                                AceType = "AddMember",
                                RightName = "WriteProperty",
                                PrincipalSID = principalSid,
                                PrincipalType = principalType,
                                IsInherited = isInherited
                            });
                        }
                    }else if (wrapper is Computer)
                    {
                        if (objectAceType == "3f78c3e5-f79a-46bd-a0b8-9d18116ddc79")
                        {
                            aces.Add(new ACL
                            {
                                AceType = "AllowedToAct",
                                RightName = "WriteProperty",
                                PrincipalSID = principalSid,
                                PrincipalType = principalType,
                                IsInherited = isInherited
                            });
                        }
                    }
                }
            }

            wrapper.Aces = aces.Distinct().ToArray();
            return wrapper;
        }

        /// <summary>
        /// Helper function to determine if an ACE actually applies to the object through inheritance
        /// </summary>
        /// <param name="ace"></param>
        /// <param name="guid"></param>
        /// <returns></returns>
        private static bool IsAceInherited(ObjectAccessRule ace, string guid)
        {
            //Ace is inherited
            if (ace.IsInherited)
            {
                //The inheritedobjecttype needs to match the guid of the object type being enumerated or the guid for All
                var inheritedType = ace.InheritedObjectType.ToString();
                return inheritedType == AllGuid || inheritedType == guid;
            }

            //The ace is NOT inherited
            //If its marked as InheritOnly, we don't want it , because it doesn't apply to the object
            if ((ace.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                return false;
            }

            //We've passed the other checks, we're good
            return true;
        }

        /// <summary>
        /// Applies pre-processing to the SID on the ACE converting sids as necessary
        /// </summary>
        /// <param name="sid"></param>
        /// <param name="objectDomain"></param>
        /// <returns></returns>
        private static string ProcessACESID(string sid)
        { 
            //Ignore Local System/Creator Owner/Principal Self
            if (sid == "S-1-5-18" || sid == "S-1-3-0" || sid == "S-1-5-10")
            {
                return null;
            }

            return sid.ToUpper();
        }
    }
}
