using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using HotelAppLambda.Models;
using HttpMultipartParser;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace HotelAppLambda;

public class HotelApp
{
    public async Task<APIGatewayProxyResponse> ListHotels(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var response = new APIGatewayProxyResponse()
        {
            Headers = new Dictionary<string, string>(),
            StatusCode = 200
        };
        
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Headers", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "OPTIONS,GET");
        response.Headers.Add("Content-Type", "application/json");

        var encodedIdToken = request.Headers["Authorization"];
        var token = encodedIdToken.StartsWith("Bearer ") ? encodedIdToken.Substring("Bearer ".Length) : encodedIdToken;
        var idToken = new JwtSecurityToken(token);
        var userIdClaim = idToken.Claims.FirstOrDefault(s => s.Type == "sub");
        var userId = userIdClaim?.Value == null ? "defaultUserId" : userIdClaim.Value;

        var region = Environment.GetEnvironmentVariable("AWS_REGION");
        var dbClient = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region));
        using var dbContext = new DynamoDBContext(dbClient);
        
        // option is to scan or query the ddb, the later is good for large db sets and required an index
        // for this project, we can just scan
        var hotels = await dbContext.ScanAsync<Hotel>(new []{new ScanCondition("UserId", ScanOperator.Equal, userId)})
            .GetRemainingAsync();
        
        response.Body = JsonSerializer.Serialize(hotels);
        
        return response;
    }

    public async Task<APIGatewayProxyResponse> AddHotel(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var response = new APIGatewayProxyResponse()
        {
            Headers = new Dictionary<string, string>(),
            StatusCode = 200
        };
        
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Headers", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "OPTIONS,POST");

        var bodyContent = request.IsBase64Encoded
            ? Convert.FromBase64String(request.Body)
            : Encoding.UTF8.GetBytes(request.Body);
        
        using var memStream = new MemoryStream(bodyContent);
        var formData = MultipartFormDataParser.Parse(memStream);
        
        var hotelName = formData.GetParameterValue("hotelName");
        var hotelRating = formData.GetParameterValue("hotelRating");
        var hotelCity = formData.GetParameterValue("hotelCity");
        var hotelPrice = formData.GetParameterValue("hotelPrice");

        var file = formData.Files.FirstOrDefault();
        var fileName = (file.Name) + '_' + DateTime.Now.ToFileTime();
        await using var fileContentStream = new MemoryStream();
        await file.Data.CopyToAsync(fileContentStream);
        fileContentStream.Position = 0; 
        
        var encodedIdToken = request.Headers["Authorization"];
        var token = encodedIdToken.StartsWith("Bearer ") ? encodedIdToken.Substring("Bearer ".Length) : encodedIdToken;
        var idToken = new JwtSecurityToken(token);
        var group = idToken.Claims.FirstOrDefault(x => x.Type == "cognito:groups");
        var usernameClaim = idToken.Claims.FirstOrDefault(c => c.Type == "cognito:username");
        var username = usernameClaim?.Value == null ? "defaultUsername" : usernameClaim.Value;
        
        if (group == null || group.Value != "Admin")
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Body = JsonSerializer.Serialize(new { Error = "Unauthorized. Must be a member of Admin group." });
        }
        
        var region = Environment.GetEnvironmentVariable("AWS_REGION");
        var bucketName = Environment.GetEnvironmentVariable("bucketName");

        var client = new AmazonS3Client(RegionEndpoint.GetBySystemName(region));
        var dbClient = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region));
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucketName,
                Key = fileName,
                InputStream = fileContentStream,
                AutoCloseStream = true,
            });
            
            var hotel = new Hotel
            {
                UserId = username,
                Id = Guid.NewGuid().ToString(),
                Name = hotelName,
                CityName = hotelCity,
                Price = int.Parse(hotelPrice),
                Rating = int.Parse(hotelRating),
                FileName = fileName
            };
            
            using var dbContext = new DynamoDBContext(dbClient);
            await dbContext.SaveAsync(hotel);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        return response;
    }
}