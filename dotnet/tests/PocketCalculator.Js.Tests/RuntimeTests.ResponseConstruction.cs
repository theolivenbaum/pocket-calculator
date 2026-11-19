using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Upstream cbcafc1 (runtime.rs): Response construction and the static factories validate as
/// Chromium does, and page script cannot give a response network metadata. Expected values
/// were measured in Chromium 157.0.8103.78 (Playwright) on the same script.
/// </summary>
public sealed partial class RuntimeTests
{
    // upstream cbcafc1, runtime.rs: response_constructor_validates_public_init_without_forging_network_metadata.
    [Fact]
    public void ResponseConstructorValidatesPublicInitWithoutForgingNetworkMetadata() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (function() {
            const error = callback => { try { callback(); return null; } catch (e) { return e.name; } };
            const response = new Response(null, {type:'opaque',url:'https://example.com/',redirected:true});
            let statusReads = 0;
            const singleRead = new Response(null, {get status(){statusReads++;return 201;}});
            return {
                ordinary:[response.type,response.url,response.redirected,String(response.status)],
                invalidStatus:[0,100,199,600,1000].map(status=>error(()=>new Response('',{status}))),
                conversion:[65736,200.9,'201'].map(status=>String(new Response(null,{status}).status)),
                invalidStatusText:['\n','\r','\0','\u0100'].map(statusText=>error(()=>new Response(null,{statusText}))),
                validStatusText:['\tOK','\u0080'].map(statusText=>new Response(null,{statusText}).statusText),
                nullBodyStatus:[204,205,304].map(status=>error(()=>new Response('',{status}))),
                nullBodyAllowed:[204,205,304].map(status=>new Response(null,{status}).body),
                nullInit:String(new Response(null,null).status),
                getter:[String(singleRead.status),String(statusReads)]
            };
        })()
        """,
        """
        {"ordinary":["default","",false,"200"],"invalidStatus":["RangeError","RangeError","RangeError","RangeError","RangeError"],"conversion":["200","200","201"],"invalidStatusText":["TypeError","TypeError","TypeError","TypeError"],"validStatusText":["\tOK","\u0080"],"nullBodyStatus":["TypeError","TypeError","TypeError"],"nullBodyAllowed":[null,null,null],"nullInit":"200","getter":["201","1"]}
        """);

    // upstream cbcafc1, runtime.rs: response_factories_validate_redirects_and_json_and_preserve_error_clones.
    [Fact]
    public void ResponseFactoriesValidateRedirectsAndJsonAndPreserveErrorClones() => UpstreamScript.AssertChromium(
        """
        <html><body></body></html>
        """,
        """
        (function() {
            const error = callback => { try { callback(); return null; } catch (e) { return e.name; } };
            const networkError = Response.error();
            const copy = networkError.clone();
            const redirect = Response.redirect('https://example.com/a/../b');
            const json = Response.json({ok:true},{headers:new Headers({'x-check':'preserved'})});
            return {
                error:[networkError.type,String(networkError.status),networkError.body],
                clone:[copy.type,String(copy.status),copy.body],
                redirect:[redirect.type,String(redirect.status),redirect.headers.get('location')],
                invalidRedirectStatus:[0,200,309,400,500].map(status=>error(()=>Response.redirect('https://example.com/',status))),
                invalidRedirectUrl:error(()=>Response.redirect('https://[invalid')),
                json:[json.type,json.headers.get('content-type'),json.headers.get('x-check')],
                jsonUndefined:error(()=>Response.json(undefined)),
                jsonSymbol:error(()=>Response.json(Symbol('value'))),
                jsonNullBodyStatus:[204,205,304].map(status=>error(()=>Response.json('body',{status})))
            };
        })()
        """,
        """
        {"error":["error","0",null],"clone":["error","0",null],"redirect":["default","302","https://example.com/b"],"invalidRedirectStatus":["RangeError","RangeError","RangeError","RangeError","RangeError"],"invalidRedirectUrl":"TypeError","json":["default","application/json","preserved"],"jsonUndefined":"TypeError","jsonSymbol":"TypeError","jsonNullBodyStatus":["TypeError","TypeError","TypeError"]}
        """);
}
