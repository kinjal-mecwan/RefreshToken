﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using RefreshToken.Auth;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace RefreshToken.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthenticateController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IConfiguration _configuration;

        public AuthenticateController(
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _configuration = configuration;
        }

        [HttpPost]
        [Route("login")]
        public async Task<IActionResult> Login([FromBody]LoginModel loginModel)
        {
            var user = await _userManager.FindByNameAsync(loginModel.Username);
            if(user != null && await _userManager.CheckPasswordAsync(user,loginModel.Password))
            {
                var userRole = await _userManager.GetRolesAsync(user);
                var authClaim = new List<Claim>()
                {
                    new Claim(ClaimTypes.Name,user.UserName),
                    new Claim(JwtRegisteredClaimNames.Jti,Guid.NewGuid().ToString())
                };
                var token=CreateToken(authClaim);
                var refershToken = GenerateRefreshToken();

                _ = int.TryParse(_configuration["JWT:RefreshTokenValidityInDays"], out int refreshTokenValidityInDays);
                user.RefreshToken = refershToken;
                user.RefreshTokenExpiryTime=DateTime.Now.AddDays(refreshTokenValidityInDays);
                await _userManager.UpdateAsync(user);

                return Ok(new
                {
                    Token=new JwtSecurityTokenHandler().WriteToken(token),
                    RefershToken=refershToken,
                    Expiration = token.ValidTo
                });
            }
            return Unauthorized();
        }

        [HttpPost]
        [Route("register")]
        public async Task<IActionResult> Register([FromBody] RegisterModel register)
        {
            var userExist = await _userManager.FindByNameAsync(register.Username);
            if (userExist != null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,new Response { Status="Error",Message="User Already Exists!"});
            }
            ApplicationUser user = new()
            {
                Email= register.Email,
                SecurityStamp=Guid.NewGuid().ToString(),
                UserName=register.Username
            };
            var res= await _userManager.CreateAsync(user,register.Password);
            if (!res.Succeeded)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new Response { Status = "Error", Message = "User creation failed! Please check user details and try again." });
            }
            return Ok(new Response { Status="Success",Message="User Created Successfully"});
        }

        [HttpPost]
        [Route("register-admin")]
        public async Task<IActionResult> RegisterAdmin([FromBody]RegisterModel registerModel)
        {
            var userRegister=await _userManager.FindByNameAsync(registerModel.Username);
            if (userRegister != null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,new Response { Status="Error",Message="User Already Exists!"});
            }
            ApplicationUser user = new()
            {
                Email = registerModel.Email,
                SecurityStamp = Guid.NewGuid().ToString(),
                UserName = registerModel.Username
            };
            var res=await _userManager.CreateAsync(user, registerModel.Password);
            if (!res.Succeeded)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new Response { Status = "Error", Message = "User creation failed! Please check user details and try again." });

            }
            if (!await _roleManager.RoleExistsAsync(UserRoles.Admin))
            {
                await _roleManager.CreateAsync(new IdentityRole(UserRoles.Admin));
            }
            if (!await _roleManager.RoleExistsAsync(UserRoles.User))
            {
                await _roleManager.CreateAsync(new IdentityRole(UserRoles.User));
            }
            if (await _roleManager.RoleExistsAsync(UserRoles.Admin))
            {
                await _userManager.AddToRoleAsync(user, UserRoles.Admin);
            }
            if (await _roleManager.RoleExistsAsync(UserRoles.Admin))
            {
                await _userManager.AddToRoleAsync(user, UserRoles.User);
            }
            return Ok(new Response { Status="Success",Message="User Created Successfully"});
        }

        [HttpPost]
        [Route("refresh-token")]
        public async Task<IActionResult> RefreshToken(TokenModel tokenModel)
        {
            if(tokenModel == null)
            {
                return BadRequest("Invalid Client Request");
            }
            string accessToken=tokenModel.AccessToken;
            string refreshToken=tokenModel.RefreshToken;
            
            var principal=GetPrincipalFromExpiredToken(accessToken);
            if (principal == null)
            {
                return BadRequest("Invalid access token or refresh token");
            }
            string usernmae = principal.Identity.Name;
            var user=await _userManager.FindByNameAsync(usernmae);
            if (user == null || user.RefreshToken !=refreshToken || user.RefreshTokenExpiryTime<=DateTime.Now)
            {
                return BadRequest("Invalid access token or refresh token");
            }
            var newAccessToken = CreateToken(principal.Claims.ToList());
            var newRefreshToken = GenerateRefreshToken();
            user.RefreshToken = newRefreshToken;
            await _userManager.UpdateAsync(user);
            return new ObjectResult(new
            {
                accessToken=new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                refreshToken=newRefreshToken
            });
        }

        [HttpPost]
        [Route("revoke/{username}")]
        public async Task<IActionResult> Revoke(string username)
        {
            var user = await _userManager.FindByEmailAsync(username);
            if (user != null)
            {
                user.RefreshToken = null;
                await _userManager.UpdateAsync(user);
            }
            return Ok(user);
        }

        [Authorize]
        [HttpPost]
        [Route("route-all")]
        public async Task<IActionResult> RevokeAll()
        {
            var users=_userManager.Users.ToList();
            foreach (var user in users)
            {
                user.RefreshToken = null;
                await _userManager.UpdateAsync(user);
            }
            return Ok(users);
        }

        private JwtSecurityToken CreateToken(List<Claim> authClaims)
        {
            var authSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]));
            _ = int.TryParse(_configuration["JWT:TokenValidityInMinutes"], out int tokenValidityInMinutes);

            var token = new JwtSecurityToken(
                issuer: _configuration["JWT:ValidIssuer"],
                audience: _configuration["JWT:ValidAudience"],
                expires: DateTime.Now.AddMinutes(tokenValidityInMinutes),
                claims: authClaims,
                signingCredentials: new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256)
                );

            return token;
        }

        private static string GenerateRefreshToken()
        {
            var randomNumber = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private ClaimsPrincipal? GetPrincipalFromExpiredToken(string? token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"])),
                ValidateLifetime = false
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out SecurityToken securityToken);
            if (securityToken is not JwtSecurityToken jwtSecurityToken || !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
                throw new SecurityTokenException("Invalid token");

            return principal;

        }
    }
}
