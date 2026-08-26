using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Contacts - the addresses alerts and reports are delivered to - and contact groups. Reads need
/// <c>contact:read</c>, writes <c>contact:write</c>. A new contact must be CONFIRMED before it receives alerts:
/// create it, have the code sent, then verify it with confirm_contact.
///
/// Deliberately absent: creating an <c>http</c> contact. Signed HTTP delivery is its own resource with its own
/// verification story - use the webhook tools instead.
/// </summary>
[McpServerToolType]
public sealed class ContactTools : V2ToolBase {
    private static readonly string[] CreatableTypes = ["email", "sms", "voiceCall", "webPush"];

    public ContactTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_contacts", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the account's contacts. Scope 'contact:read'. An unconfirmed contact receives nothing until " +
        "it is confirmed.")]
    public Task<CallToolResult> ListContacts(
        [Description("Free-text search over name and address.")] string? q = null,
        [Description("Comma-separated contact types, e.g. 'email,sms'.")] string? type = null,
        [Description("Keep only confirmed (true) or only unconfirmed (false) contacts.")] bool? confirmed = null,
        [Description("Comma-separated contact ids.")] string? id = null,
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/contact",
            new Q().Set("q", q).List("type", type).Set("confirmed", confirmed).List("id", id)
                   .Set("limit", CapLimit(limit)).Set("cursor", cursor),
            "list_contacts", "Contacts:", ct);

    [McpServerTool(Name = "get_contact", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read one contact. Scope 'contact:read'. Add expand='subscription' to see what it is subscribed to.")]
    public Task<CallToolResult> GetContact(
        [Description("The contact id.")] string id,
        [Description("Comma-separated expand tokens: subscription, template, group.")] string? expand = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/contact/{Uri.EscapeDataString(id)}", new Q().Csv("expand", expand), heading: "Contact:", ct: ct);

    [McpServerTool(Name = "create_contact", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Create a contact of type email, sms, voiceCall or webPush. Scope 'contact:write'. Sending to a " +
        "person's address is a real-world action - confirm the address with the user first. The contact is created " +
        "UNCONFIRMED: a confirmation code is sent to it, and confirm_contact must be called with that code before " +
        "it receives alerts. For signed HTTP delivery use create_webhook instead.")]
    public async Task<CallToolResult> CreateContact(
        [Description("Contact type: email, sms, voiceCall or webPush.")] string type,
        [Description("The address: an email address, or a phone number in international format.")] string address,
        [Description("Display name.")] string? name = null,
        [Description("Message language code, e.g. 'en'.")] string? language = null,
        [Description("Delay in minutes before an alert is sent to this contact.")] int? alertDelay = null,
        CancellationToken ct = default) {

        if (!CreatableTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return Err($"'{ToolText.Sanitize(type, 40)}' is not a contact type this tool creates. Use one of: {string.Join(", ", CreatableTypes)}. " +
                       "Messenger contacts (telegram, viber, discord, ...) are registered from the messenger itself, and HTTP delivery is a webhook - see create_webhook.");

        var body = Body(("type", type), ("address", address), ("name", name), ("language", language), ("alertDelay", alertDelay));
        return await CallAsync(HttpMethod.Post, "/contact", null, body,
            heading: "Contact created (confirm it before it receives alerts):", ct: ct);
    }

    [McpServerTool(Name = "update_contact", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Partially update a contact. Scope 'contact:write'. Changing the address re-triggers confirmation.")]
    public async Task<CallToolResult> UpdateContact(
        [Description("The contact id.")] string id,
        [Description("New display name.")] string? name = null,
        [Description("New address.")] string? address = null,
        [Description("New message language code.")] string? language = null,
        [Description("New alert delay in minutes.")] int? alertDelay = null,
        [Description("Group several alerts into one message.")] bool? groupedAlerts = null,
        CancellationToken ct = default) {

        var body = Body(("name", name), ("address", address), ("language", language),
                        ("alertDelay", alertDelay), ("groupedAlerts", groupedAlerts));
        if (body.Count == 0) return Err("Nothing to update - pass at least one field to change.");
        return await CallAsync(HttpMethod.Patch, $"/contact/{Uri.EscapeDataString(id)}", null, body, heading: "Contact updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_contact", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Delete a contact and every subscription it had. Scope 'contact:write'. DESTRUCTIVE - confirm with " +
        "the user first (their monitors stop notifying that address), then report the receipt this returns.")]
    public Task<CallToolResult> DeleteContact(
        [Description("The contact id.")] string id,
        [Description("Must be true to actually delete. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("contact", $"/contact/{Uri.EscapeDataString(id)}", $"/contact/{Uri.EscapeDataString(id)}", confirmed, ct: ct);

    [McpServerTool(Name = "send_contact_confirmation", Destructive = false, ReadOnly = false, OpenWorld = true)]
    [Description("Send (or resend) the confirmation code to an unconfirmed contact. Scope 'contact:write'. This " +
        "delivers a real message to the address; the code itself is never returned to the agent - ask the user for it.")]
    public Task<CallToolResult> SendContactConfirmation(
        [Description("The contact id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/contact/{Uri.EscapeDataString(id)}/confirmation", heading: "Confirmation requested:", ct: ct);

    [McpServerTool(Name = "confirm_contact", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Confirm a contact with the code it received. Scope 'contact:write'. Ask the user to read the code " +
        "from their inbox or phone.")]
    public Task<CallToolResult> ConfirmContact(
        [Description("The contact id.")] string id,
        [Description("The confirmation code the contact received.")] string code,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/contact/{Uri.EscapeDataString(id)}/confirmation/verify", null,
            Body(("code", code)), heading: "Contact confirmed:", ct: ct);

    [McpServerTool(Name = "test_contact", Destructive = false, ReadOnly = false, OpenWorld = true)]
    [Description("Send a real test alert to a confirmed contact and report how the delivery ended. Scope " +
        "'contact:write'. This actually messages the person and may cost account balance for sms/voice - ask first.")]
    public Task<CallToolResult> TestContact(
        [Description("The contact id.")] string id,
        [Description("Which alert to simulate: up, down or repeatedlyDown.")] string? alertType = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/contact/{Uri.EscapeDataString(id)}/test", null,
            Body(("alertType", alertType)), heading: "Test delivery:", untrusted: true, ct: ct);

    [McpServerTool(Name = "list_contact_groups", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the account's contact groups. Scope 'contact:read'.")]
    public Task<CallToolResult> ListContactGroups(
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/contact/group", new Q().Set("limit", CapLimit(limit)).Set("cursor", cursor),
            "list_contact_groups", "Contact groups:", ct);

    [McpServerTool(Name = "create_contact_group", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Create a contact group: a named set of contacts, each with the events it should receive. Scope " +
        "'contact:write'.")]
    public async Task<CallToolResult> CreateContactGroup(
        [Description("Group name.")] string name,
        [Description("JSON array of members, e.g. [{\"contact\":\"<contactId>\",\"events\":[\"down\",\"up\"]}]. Events: up, down, repeatedlyDown, daily, weekly, monthly, quarterly, yearly.")] string itemsJson,
        CancellationToken ct = default) {

        if (!TryJson(itemsJson, nameof(itemsJson), out var items, out var badJson)) return Err(badJson);
        if (items.ValueKind != System.Text.Json.JsonValueKind.Array) return Err("'itemsJson' must be a JSON array of members.");
        return await CallAsync(HttpMethod.Post, "/contact/group", null, Body(("name", name), ("items", items)),
            heading: "Contact group created:", ct: ct);
    }

    [McpServerTool(Name = "update_contact_group", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Rename a contact group and/or REPLACE its membership. Scope 'contact:write'. A members list " +
        "replaces the whole set, it does not merge.")]
    public async Task<CallToolResult> UpdateContactGroup(
        [Description("The group id.")] string id,
        [Description("New group name.")] string? name = null,
        [Description("JSON array of members that replaces the current set.")] string? itemsJson = null,
        CancellationToken ct = default) {

        if (!TryJson(itemsJson, nameof(itemsJson), out var items, out var badJson)) return Err(badJson);
        var body = Body(("name", name), ("items", items));
        if (body.Count == 0) return Err("Nothing to update - pass a new name or a new member list.");
        return await CallAsync(HttpMethod.Patch, $"/contact/group/{Uri.EscapeDataString(id)}", null, body,
            heading: "Contact group updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_contact_group", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Delete a contact group. Scope 'contact:write'. DESTRUCTIVE - confirm with the user first. The " +
        "contacts themselves are not deleted.")]
    public Task<CallToolResult> DeleteContactGroup(
        [Description("The group id.")] string id,
        [Description("Must be true to actually delete. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("contact group", $"/contact/group/{Uri.EscapeDataString(id)}", $"/contact/group/{Uri.EscapeDataString(id)}", confirmed, ct: ct);
}
