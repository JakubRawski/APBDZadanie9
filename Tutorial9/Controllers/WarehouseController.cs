using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Tutorial9.Model.DTO;

namespace Tutorial9.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WarehouseController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public WarehouseController(IConfiguration configuration)
    {
        _configuration = configuration;
    }
    [HttpPost("procedura")]
    public async Task<IActionResult> AddProductUsingProcedure([FromBody] WarehouseRequest request)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("Default"));
        await using var command = new SqlCommand("AddProductToWarehouse", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@IdProduct", request.IdProduct);
        command.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
        command.Parameters.AddWithValue("@Amount", request.Amount);
        command.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);

        try
        {
            await connection.OpenAsync();
            var result = await command.ExecuteScalarAsync();
            return Ok(result);
        }
        catch (SqlException ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpPost("normal")]
    public async Task<IActionResult> AddProductToWarehouse([FromBody] WarehouseRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest("Amount must be greater than zero.");

        await using var connection = new SqlConnection(_configuration.GetConnectionString("Default"));
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();

        try
        {
            var command = new SqlCommand { Connection = connection, Transaction = (SqlTransaction)transaction };

            command.CommandText = "SELECT COUNT(1) FROM Product WHERE IdProduct = @IdProduct";
            command.Parameters.AddWithValue("@IdProduct", request.IdProduct);
            var productExists = (int)await command.ExecuteScalarAsync() > 0;
            if (!productExists) return NotFound("Product not found.");
            command.Parameters.Clear();

            command.CommandText = "SELECT COUNT(1) FROM Warehouse WHERE IdWarehouse = @IdWarehouse";
            command.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
            var warehouseExists = (int)await command.ExecuteScalarAsync() > 0;
            if (!warehouseExists) return NotFound("Warehouse not found.");
            command.Parameters.Clear();

            command.CommandText = @"SELECT TOP 1 IdOrder, Price FROM ""Order"" o 
                JOIN Product p ON o.IdProduct = p.IdProduct
                WHERE o.IdProduct = @IdProduct AND o.Amount = @Amount AND o.CreatedAt < @CreatedAt AND o.FulfilledAt IS NULL";
            command.Parameters.AddWithValue("@IdProduct", request.IdProduct);
            command.Parameters.AddWithValue("@Amount", request.Amount);
            command.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);

            int? orderId = null;
            decimal? price = null;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    orderId = reader.GetInt32(0);

                    price = reader.GetDecimal(1);

                }
            }
            
            if (orderId == null) return NotFound("No matching order found.");
            command.Parameters.Clear();

            command.CommandText = "SELECT COUNT(1) FROM Product_Warehouse WHERE IdOrder = @IdOrder";
           
            command.Parameters.AddWithValue("@IdOrder", orderId);
            var alreadyFulfilled = (int)await command.ExecuteScalarAsync() > 0;
            if (alreadyFulfilled) return Conflict("Order already fulfilled.");
            command.Parameters.Clear();

            command.CommandText = "UPDATE \"Order\" SET FulfilledAt = GETDATE() WHERE IdOrder = @IdOrder";
            command.Parameters.AddWithValue("@IdOrder", orderId);
            await command.ExecuteNonQueryAsync();
            command.Parameters.Clear();

            command.CommandText = @"
                INSERT INTO Product_Warehouse (IdWarehouse, IdProduct, IdOrder, Amount, Price, CreatedAt)
                OUTPUT INSERTED.IdProductWarehouse
                VALUES (@IdWarehouse, @IdProduct, @IdOrder, @Amount, @Price, GETDATE())";
            command.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
            command.Parameters.AddWithValue("@IdProduct", request.IdProduct);
            command.Parameters.AddWithValue("@IdOrder", orderId);
            command.Parameters.AddWithValue("@Amount", request.Amount);
            command.Parameters.AddWithValue("@Price", price * request.Amount);

            var newId = (int)await command.ExecuteScalarAsync();

            await transaction.CommitAsync();
            return Ok(newId);
        }
        catch (Exception e)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, e.Message);
        }
    }
}