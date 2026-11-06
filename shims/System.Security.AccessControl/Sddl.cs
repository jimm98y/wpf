// Security descriptors as SDDL text (O:owner G:group D:flags(ace)... S:flags(ace)...), read and written in managed
// code where Windows' advapi32 is not: the ACE types, flags, rights and SID aliases Windows writes and reads.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace System.Security.AccessControl
{
    internal static class Sddl
    {
        private static readonly Dictionary<string, string> s_aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["WD"] = "S-1-1-0", ["CO"] = "S-1-3-0", ["CG"] = "S-1-3-1", ["OW"] = "S-1-3-4", ["NU"] = "S-1-5-2", ["IU"] = "S-1-5-4",
            ["SU"] = "S-1-5-6", ["AN"] = "S-1-5-7", ["PS"] = "S-1-5-10", ["AU"] = "S-1-5-11", ["RC"] = "S-1-5-12", ["SY"] = "S-1-5-18",
            ["LS"] = "S-1-5-19", ["NS"] = "S-1-5-20", ["BA"] = "S-1-5-32-544", ["BU"] = "S-1-5-32-545", ["BG"] = "S-1-5-32-546",
            ["PU"] = "S-1-5-32-547", ["AO"] = "S-1-5-32-548", ["SO"] = "S-1-5-32-549", ["PO"] = "S-1-5-32-550", ["BO"] = "S-1-5-32-551",
            ["RE"] = "S-1-5-32-552", ["RU"] = "S-1-5-32-554", ["RD"] = "S-1-5-32-555", ["NO"] = "S-1-5-32-556", ["MU"] = "S-1-5-32-558",
            ["LU"] = "S-1-5-32-559", ["IS"] = "S-1-5-32-568", ["CY"] = "S-1-5-32-569", ["ER"] = "S-1-5-32-573", ["AC"] = "S-1-15-2-1",
        };
        private static readonly Dictionary<string, string> s_aliasOf = s_aliases.ToDictionary(p => p.Value, p => p.Key.ToUpperInvariant());

        private static readonly (string Code, int Mask)[] s_rights =
        {
            ("GA", 0x10000000), ("GR", unchecked((int)0x80000000)), ("GW", 0x40000000), ("GX", 0x20000000),
            ("RC", 0x20000), ("SD", 0x10000), ("WD", 0x40000), ("WO", 0x80000),
            ("RP", 0x10), ("WP", 0x20), ("CC", 0x1), ("DC", 0x2), ("LC", 0x4), ("SW", 0x8), ("LO", 0x80), ("DT", 0x40), ("CR", 0x100),
            ("FA", 0x1F01FF), ("FR", 0x120089), ("FW", 0x120116), ("FX", 0x1200A0),
            ("KA", 0xF003F), ("KR", 0x20019), ("KW", 0x20006), ("KX", 0x20019),
        };

        private static readonly (string Code, AceFlags Flag)[] s_aceFlags =
        {
            ("CI", AceFlags.ContainerInherit), ("OI", AceFlags.ObjectInherit), ("NP", AceFlags.NoPropagateInherit),
            ("IO", AceFlags.InheritOnly), ("ID", AceFlags.Inherited), ("SA", AceFlags.SuccessfulAccess), ("FA", AceFlags.FailedAccess),
        };

        // ---- reading ---------------------------------------------------------------------------------------

        public static byte[] Parse(string sddl)
        {
            SecurityIdentifier? owner = null, group = null;
            RawAcl? dacl = null, sacl = null;
            ControlFlags flags = ControlFlags.SelfRelative;
            int i = 0;
            string s = sddl.Trim();
            while (i < s.Length)
            {
                if (i + 1 >= s.Length || s[i + 1] != ':') throw Invalid();
                char section = char.ToUpperInvariant(s[i]);
                i += 2;
                int end = NextSection(s, i);
                string body = s[i..end];
                i = end;
                switch (section)
                {
                    case 'O': owner = Sid(body); break;
                    case 'G': group = Sid(body); break;
                    case 'D': dacl = Acl(body, ref flags, dacl: true); flags |= dacl != null ? ControlFlags.DiscretionaryAclPresent : 0; break;
                    case 'S': sacl = Acl(body, ref flags, dacl: false); flags |= sacl != null ? ControlFlags.SystemAclPresent : 0; break;
                    default: throw Invalid();
                }
            }
            var sd = new RawSecurityDescriptor(flags, owner, group, sacl, dacl);
            var binary = new byte[sd.BinaryLength];
            sd.GetBinaryForm(binary, 0);
            return binary;
        }

        // the next "X:" outside parentheses
        private static int NextSection(string s, int from)
        {
            int depth = 0;
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (depth == 0 && i + 1 < s.Length && s[i + 1] == ':' && "OGDSogds".IndexOf(s[i]) >= 0 && i > from) return i;
            }
            return s.Length;
        }

        private static RawAcl? Acl(string body, ref ControlFlags flags, bool dacl)
        {
            int open = body.IndexOf('(');
            string head = open < 0 ? body : body[..open];
            for (int i = 0; i < head.Length;)
            {
                if (head.Substring(i).StartsWith("NO_ACCESS_CONTROL", StringComparison.OrdinalIgnoreCase)) return null;
                string f = head.Substring(i, Math.Min(2, head.Length - i)).ToUpperInvariant();
                if (head[i] == 'P' || head[i] == 'p') { flags |= dacl ? ControlFlags.DiscretionaryAclProtected : ControlFlags.SystemAclProtected; i++; continue; }
                if (f == "AI") flags |= dacl ? ControlFlags.DiscretionaryAclAutoInherited : ControlFlags.SystemAclAutoInherited;
                else if (f == "AR") flags |= dacl ? ControlFlags.DiscretionaryAclAutoInheritRequired : ControlFlags.SystemAclAutoInheritRequired;
                else throw Invalid();
                i += 2;
            }
            var acl = new RawAcl(GenericAcl.AclRevision, 0);
            if (open < 0) return acl;
            foreach (string ace in body[open..].Split(')', StringSplitOptions.RemoveEmptyEntries))
            {
                string a = ace.TrimStart('(');
                string[] f = a.Split(';');
                if (f.Length < 6) throw Invalid();
                AceFlags aceFlags = AceFlags.None;
                for (int k = 0; k + 1 < f[1].Length + 1 && k < f[1].Length; k += 2)
                {
                    string code = f[1].Substring(k, 2).ToUpperInvariant();
                    var match = s_aceFlags.FirstOrDefault(x => x.Code == code);
                    if (match.Code == null) throw Invalid();
                    aceFlags |= match.Flag;
                }
                int rights = Rights(f[2]);
                SecurityIdentifier sid = Sid(f[5]);
                bool isObject = f[3].Length > 0 || f[4].Length > 0;
                string type = f[0].ToUpperInvariant();
                GenericAce made = type switch
                {
                    "A" when !isObject => new CommonAce(aceFlags, AceQualifier.AccessAllowed, rights, sid, false, null),
                    "D" when !isObject => new CommonAce(aceFlags, AceQualifier.AccessDenied, rights, sid, false, null),
                    "AU" when !isObject => new CommonAce(aceFlags, AceQualifier.SystemAudit, rights, sid, false, null),
                    "AL" when !isObject => new CommonAce(aceFlags, AceQualifier.SystemAlarm, rights, sid, false, null),
                    "A" or "OA" => Object(aceFlags, AceQualifier.AccessAllowed, rights, sid, f[3], f[4]),
                    "D" or "OD" => Object(aceFlags, AceQualifier.AccessDenied, rights, sid, f[3], f[4]),
                    "AU" or "OU" => Object(aceFlags, AceQualifier.SystemAudit, rights, sid, f[3], f[4]),
                    _ => throw Invalid(),
                };
                acl.InsertAce(acl.Count, made);
            }
            return acl;
        }

        private static ObjectAce Object(AceFlags flags, AceQualifier q, int rights, SecurityIdentifier sid, string objectType, string inheritedType)
        {
            ObjectAceFlags present = (objectType.Length > 0 ? ObjectAceFlags.ObjectAceTypePresent : 0) | (inheritedType.Length > 0 ? ObjectAceFlags.InheritedObjectAceTypePresent : 0);
            return new ObjectAce(flags, q, rights, sid, present, objectType.Length > 0 ? Guid.Parse(objectType) : Guid.Empty,
                inheritedType.Length > 0 ? Guid.Parse(inheritedType) : Guid.Empty, false, null);
        }

        private static int Rights(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return unchecked((int)uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (text.Length > 0 && char.IsAsciiDigit(text[0])) return unchecked((int)uint.Parse(text, CultureInfo.InvariantCulture));
            int mask = 0;
            for (int i = 0; i < text.Length; i += 2)
            {
                if (i + 2 > text.Length) throw Invalid();
                string code = text.Substring(i, 2).ToUpperInvariant();
                var match = s_rights.FirstOrDefault(r => r.Code == code);
                if (match.Code == null) throw Invalid();
                mask |= match.Mask;
            }
            return mask;
        }

        private static SecurityIdentifier Sid(string text)
        {
            string t = text.Trim();
            if (s_aliases.TryGetValue(t, out string? sid)) t = sid;
            try { return new SecurityIdentifier(t); }
            catch (ArgumentException) { throw new ArgumentException(SR.AccessControl_InvalidSidInSDDLString, "sddlForm"); }
        }

        private static ArgumentException Invalid() => new ArgumentException(SR.ArgumentException_InvalidSDSddlForm, "sddlForm");

        // ---- writing ---------------------------------------------------------------------------------------

        public static string Write(RawSecurityDescriptor sd, SecurityInfos si)
        {
            var sb = new StringBuilder();
            if ((si & SecurityInfos.Owner) != 0 && sd.Owner != null) sb.Append("O:").Append(Sid(sd.Owner));
            if ((si & SecurityInfos.Group) != 0 && sd.Group != null) sb.Append("G:").Append(Sid(sd.Group));
            if ((si & SecurityInfos.DiscretionaryAcl) != 0 && (sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0)
            {
                sb.Append("D:");
                if ((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0) sb.Append('P');
                if ((sd.ControlFlags & ControlFlags.DiscretionaryAclAutoInheritRequired) != 0) sb.Append("AR");
                if ((sd.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0) sb.Append("AI");
                if (sd.DiscretionaryAcl == null) sb.Append("NO_ACCESS_CONTROL");
                else foreach (GenericAce ace in sd.DiscretionaryAcl) Ace(sb, ace);
            }
            if ((si & SecurityInfos.SystemAcl) != 0 && (sd.ControlFlags & ControlFlags.SystemAclPresent) != 0)
            {
                sb.Append("S:");
                if ((sd.ControlFlags & ControlFlags.SystemAclProtected) != 0) sb.Append('P');
                if ((sd.ControlFlags & ControlFlags.SystemAclAutoInheritRequired) != 0) sb.Append("AR");
                if ((sd.ControlFlags & ControlFlags.SystemAclAutoInherited) != 0) sb.Append("AI");
                if (sd.SystemAcl != null) foreach (GenericAce ace in sd.SystemAcl) Ace(sb, ace);
            }
            return sb.ToString();
        }

        private static string Sid(SecurityIdentifier sid) => s_aliasOf.TryGetValue(sid.Value, out string? a) ? a : sid.Value;

        private static void Ace(StringBuilder sb, GenericAce ace)
        {
            string flags = string.Concat(s_aceFlags.Where(f => (ace.AceFlags & f.Flag) != 0).Select(f => f.Code));
            switch (ace)
            {
                case ObjectAce o:
                    string t = o.AceQualifier switch { AceQualifier.AccessAllowed => "OA", AceQualifier.AccessDenied => "OD", _ => "OU" };
                    sb.Append('(').Append(t).Append(';').Append(flags).Append(";0x").Append(o.AccessMask.ToString("x", CultureInfo.InvariantCulture)).Append(';')
                      .Append((o.ObjectAceFlags & ObjectAceFlags.ObjectAceTypePresent) != 0 ? o.ObjectAceType.ToString() : "").Append(';')
                      .Append((o.ObjectAceFlags & ObjectAceFlags.InheritedObjectAceTypePresent) != 0 ? o.InheritedObjectAceType.ToString() : "").Append(';')
                      .Append(Sid(o.SecurityIdentifier)).Append(')');
                    break;
                case QualifiedAce q:
                    string type = q.AceQualifier switch { AceQualifier.AccessAllowed => "A", AceQualifier.AccessDenied => "D", AceQualifier.SystemAudit => "AU", _ => "AL" };
                    sb.Append('(').Append(type).Append(';').Append(flags).Append(";0x").Append(q.AccessMask.ToString("x", CultureInfo.InvariantCulture))
                      .Append(";;;").Append(Sid(q.SecurityIdentifier)).Append(')');
                    break;
            }
        }
    }
}
