﻿using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpHound3.JSON;

namespace SharpHound3.LdapWrappers
{
    internal class GPO : LdapWrapper
    {
        internal GPO(SearchResultEntry entry) : base(entry)
        {
            
        }

        
    }
}
