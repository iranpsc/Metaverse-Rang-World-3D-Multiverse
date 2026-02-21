public class ProfileResult
{
    public bool IsSuccess { get; private set; }
    public UserProfileResponse Response { get; private set; }
    public string ErrorMessage { get; private set; }

    private ProfileResult(bool ok, UserProfileResponse resp, string err)
    {
        IsSuccess = ok;
        Response = resp;
        ErrorMessage = err;
    }

    public static ProfileResult Success(UserProfileResponse resp)
        => new ProfileResult(true, resp, null);

    public static ProfileResult Failure(string err)
        => new ProfileResult(false, null, err);
}
