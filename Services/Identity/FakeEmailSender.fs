namespace GameTime.Services.Identity

open System.Threading.Tasks
open System.Web
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Logging

type FakeEmailSender(logger: ILogger<FakeEmailSender>) =
    interface IEmailSender<AppUser> with
        member this.SendConfirmationLinkAsync(user, email, confirmationLink) =
            logger.LogInformation($"Email confirmation: UserId={user.Id} email={email} confirmationLink={HttpUtility.HtmlDecode(confirmationLink)}")
            Task.FromResult(())
            
        member this.SendPasswordResetCodeAsync(user, email, resetCode) =
            logger.LogInformation($"Password reset code: UserId={user.Id} email={email} resetCode={resetCode}")
            Task.FromResult(())
            
        member this.SendPasswordResetLinkAsync(user, email, resetLink) =
            logger.LogInformation($"Password reset link: UserId={user.Id} email={email} resetLink={HttpUtility.HtmlDecode(resetLink)}")
            Task.FromResult(())
