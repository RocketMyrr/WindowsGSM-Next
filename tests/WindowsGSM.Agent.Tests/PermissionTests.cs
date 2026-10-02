using System.Net;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Roles, grants and machine scoping.</summary>
[Collection("Agent")]
public class PermissionTests
{
    private readonly AgentFixture _f;
    public PermissionTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task A_viewer_sees_everything_and_changes_nothing()
    {
        var viewer = await _f.UserAsync("viewer1", Role.Viewer);
        var servers = await viewer.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers");
        Assert.All(AgentFixture.ServerIds, id => Assert.Contains(servers, s => s.Id == id)); // (other tests may install more)
        Assert.All(servers, s => Assert.Equal(Capability.View, s.Can));

        var start = await viewer.PostAsync(_f.ServerUrl("102", "/start"));
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(_f.ServerUrl("102", "/console"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(_f.ServerUrl("102", "/files"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/audit")).StatusCode);
    }

    [Fact]
    public async Task A_member_only_sees_the_servers_granted_and_others_look_missing()
    {
        var grants = new Dictionary<string, Capability> { [$"{_f.MachineId}/102"] = Capability.View | Capability.Console };
        var member = await _f.UserAsync("member1", Role.Member, grants);

        var servers = await member.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers");
        Assert.Equal(new[] { "102" }, servers.Select(s => s.Id).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(_f.ServerUrl("102", "/console"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(_f.ServerUrl("101"))).StatusCode); // not 403: existence isn't revealed
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/v2/machines/local/servers", new InstallRequest("x", "y"))).StatusCode);
    }

    [Fact]
    public async Task Grants_on_the_whole_machine_apply_to_every_server()
    {
        var grants = new Dictionary<string, Capability> { [$"{_f.MachineId}/*"] = Capability.View | Capability.Files };
        var member = await _f.UserAsync("member2", Role.Member, grants);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(_f.ServerUrl("103", "/files"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(_f.ServerUrl("103", "/settings"))).StatusCode);
    }

    [Fact]
    public async Task Routes_are_machine_scoped()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Owner.GetAsync("/api/v2/machines/some-other-box/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Owner.GetAsync($"/api/v2/machines/{_f.MachineId}/servers")).StatusCode);

        var machines = await _f.Owner.GetJsonAsync<List<MachineDto>>("/api/v2/machines");
        var local = Assert.Single(machines);
        Assert.Equal(_f.MachineId, local.Id);
        Assert.True(local.IsLocal);

        var servers = await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers");
        Assert.All(servers, s => Assert.Equal(_f.MachineId, s.Machine));
    }

    [Fact]
    public async Task Admins_manage_lower_roles_but_only_owners_manage_admins_and_owners()
    {
        var admin = await _f.UserAsync("admin1", Role.Admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/v2/users", new UserRequest("op-by-admin", Role.Operator, true, null, "long enough pw"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/users", new UserRequest("admin-by-admin", Role.Admin, true, null, "long enough pw"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsync($"/api/v2/users/{AgentFixture.OwnerName}", new UserRequest(AgentFixture.OwnerName, Role.Viewer, true, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v2/agent/settings")).StatusCode);
    }

    [Fact]
    public async Task There_is_always_an_enabled_owner()
    {
        var res = await _f.Owner.PutAsync($"/api/v2/users/{AgentFixture.OwnerName}", new UserRequest(AgentFixture.OwnerName, Role.Admin, true, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.DeleteAsync($"/api/v2/users/{AgentFixture.OwnerName}")).StatusCode);
    }

    [Fact]
    public async Task Bad_grant_scopes_are_rejected()
    {
        var res = await _f.Owner.PostAsync("/api/v2/users", new UserRequest("badscope", Role.Member,
            true, new Dictionary<string, Capability> { ["just-a-server"] = Capability.View }, "long enough pw"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_signs_them_out_at_once()
    {
        var op = await _f.UserAsync("tobedisabled", Role.Operator);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v2/auth/me")).StatusCode);
        await _f.Owner.PutAsync("/api/v2/users/tobedisabled", new UserRequest("tobedisabled", Role.Operator, false, null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await op.GetAsync("/api/v2/auth/me")).StatusCode);
    }
}
