using System.Linq;
using System.Text.RegularExpressions;

namespace Rests
{
    public class RestCommand
    {
        private readonly RestCommandD callback;

        public string Name { get; protected set; }
        public string UriTemplate { get; protected set; }
        public string UriVerbMatch { get; protected set; }
        public string[] UriVerbs { get; protected set; }
        public virtual bool RequiresToken => false;
        public bool DoLog { get; set; }

        public RestCommand(string name, string uritemplate, RestCommandD callback)
        {
            Name = name;
            UriTemplate = uritemplate;
            UriVerbMatch = string.Format("^{0}$", string.Join("([^/]*)", Regex.Split(uritemplate, "\\{[^\\{\\}]*\\}")));
            var matches = Regex.Matches(uritemplate, "\\{([^\\{\\}]*)\\}");
            UriVerbs = matches.Select(match => match.Groups[1].Value).ToArray();
            this.callback = callback;
            DoLog = true;
        }

        public RestCommand(string uritemplate, RestCommandD callback)
            : this(string.Empty, uritemplate, callback)
        {
        }

        public bool HasVerbs => UriVerbs.Length > 0;

        public virtual object Execute(
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            RestHttpRequest request,
            RestHttpContext context) =>
            callback(new RestRequestArgs(verbs, parameters, request, context));
    }

    public class SecureRestCommand : RestCommand
    {
        private readonly RestCommandD secureCallback;

        public override bool RequiresToken => true;
        public string[] Permissions { get; set; }

        public SecureRestCommand(string name, string uritemplate, RestCommandD callback, params string[] permissions)
            : base(name, uritemplate, _ => new RestObject("401")
            {
                Error = GetString("Not authorized. The specified API endpoint requires a token.")
            })
        {
            secureCallback = callback;
            Permissions = permissions;
        }

        public SecureRestCommand(string uritemplate, RestCommandD callback, params string[] permissions)
            : this(string.Empty, uritemplate, callback, permissions)
        {
        }

        public object Execute(
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            SecureRest.TokenData tokenData,
            RestHttpRequest request,
            RestHttpContext context)
        {
            if (tokenData.Equals(SecureRest.TokenData.None))
            {
                return new RestObject("401")
                {
                    Error = GetString("Not authorized. The specified API endpoint requires a token.")
                };
            }

            return secureCallback(new RestRequestArgs(verbs, parameters, request, tokenData, context));
        }
    }
}
